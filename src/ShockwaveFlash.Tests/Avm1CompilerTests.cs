using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Analysis;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf6;
using ShockwaveFlash.Avm1.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1CompilerTests
{
    [Fact]
    public void Compiles_literal_return_through_mir_stack_lir_and_assembler()
    {
        var builder = new Avm1SourceArena.Builder();
        var expressionOrigin = builder.AddOrigin(0, 0, 10, 14);
        var returnOrigin = builder.AddOrigin(0, 1, 5, 15);
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 42),
            origin: expressionOrigin);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: literal,
            origin: returnOrigin);
        var method = CreateMethod(builder, [returnStatement]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
            Avm1MirInstructionKind.Constant,
            Avm1MirInstructionKind.Return
        ]);
        artifact.StackLir.ShouldNotBeNull().Instructions
            .Select(instruction => instruction.Kind).ShouldBe([
                Avm1StackLirInstructionKind.PushConstant,
                Avm1StackLirInstructionKind.Action
            ]);
        artifact.ActionBody.ShouldNotBeNull().Actions.Count.ShouldBe(2);
        artifact.ActionBody.Actions[0].ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBe(PushValue.Integer(42));
        artifact.ActionBody.Actions[1].ShouldBeOfType<ActionReturn>();
        artifact.StackLir.MaximumStackDepth.ShouldBe(1);
        artifact.ActionBody.MaximumStackDepth.ShouldBe(1);
        artifact.InstructionMap.Select(mapping => mapping.Origin).ShouldBe([
            expressionOrigin,
            returnOrigin
        ]);
        var sourceMapUnit = artifact.SourceMap.CodeUnits.ShouldHaveSingleItem();
        sourceMapUnit.Kind.ShouldBe(Avm1CompiledCodeUnitKind.Method);
        sourceMapUnit.ByteOffset.ShouldBe(0);
        sourceMapUnit.ByteLength.ShouldBe(artifact.Bytecode.Length);
        artifact.SourceMap.Entries.Select(entry => entry.Origin).ShouldBe([
            expressionOrigin,
            returnOrigin
        ]);
        for (var i = 0; i < artifact.SourceMap.Entries.Count; i++)
        {
            var entry = artifact.SourceMap.Entries[i];
            var location = artifact.ActionBody.ActionLocations[i];
            entry.ActionIndex.ShouldBe(location.ActionIndex);
            entry.ByteOffset.ShouldBe(location.ByteOffset);
            entry.ByteLength.ShouldBe(location.ByteLength);
            artifact.SourceMap.TryGetEntry(entry.ByteOffset, out var resolved)
                .ShouldBeTrue();
            resolved.ShouldBe(entry);
        }
        artifact.SourceMap.GetEntries(expressionOrigin)
            .ShouldHaveSingleItem()
            .ShouldBe(artifact.SourceMap.Entries[0]);
        artifact.SourceMap.TryGetOrigin(
            artifact.SourceMap.Entries[0].ByteOffset,
            out var resolvedOrigin).ShouldBeTrue();
        resolvedOrigin.ShouldBe(artifact.Source.Arena[expressionOrigin]);
        DecompileBack(artifact).ShouldContain("return 42;");
    }

    [Fact]
    public void Preserves_left_to_right_reads_and_declares_a_named_local()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var left = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "left",
            SourceTypeIndex.Invalid,
            unknown);
        var right = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "right",
            SourceTypeIndex.Invalid,
            unknown);
        var total = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "total",
            SourceTypeIndex.Invalid,
            unknown);
        var leftReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: left);
        var rightReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: right);
        var add = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children: [leftReference, rightReference]);
        var declaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: add,
            symbol: total);
        var totalReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: total);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: totalReference);
        var method = CreateMethod(
            builder,
            [declaration, returnStatement],
            [left, right]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.Binary,
            Avm1MirInstructionKind.DeclareLocal,
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.Return
        ]);
        var loads = artifact.Mir.Instructions
            .Where(instruction => instruction.Kind is Avm1MirInstructionKind.LoadLValue)
            .Select(instruction => artifact.Mir[
                artifact.Mir[instruction.LValue].Name])
            .ToArray();
        loads.ShouldBe(["left", "right", "total"]);
        artifact.Mir.Instructions[0].Effects.ShouldBe(Avm1MirEffect.ReadsActivation);
        artifact.Mir.Instructions[1].Effects.ShouldBe(Avm1MirEffect.ReadsActivation);
        artifact.Mir.Instructions[2].Effects.ShouldBe(
            Avm1MirEffect.MayInvokeUserCode | Avm1MirEffect.MayThrow);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Add2,
                ActionOpcode.Push,
                ActionOpcode.StackSwap,
                ActionOpcode.DefineLocal,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Return
            ]);
        GetPushedString(artifact.ActionBody.Actions[0]).ShouldBe("left");
        GetPushedString(artifact.ActionBody.Actions[2]).ShouldBe("right");
        GetPushedString(artifact.ActionBody.Actions[5]).ShouldBe("total");
        var roundTrip = DecompileBack(artifact);
        roundTrip.ShouldContain("var total = left + right;");
        roundTrip.ShouldContain("return total;");
    }

    [Fact]
    public void Assignment_expression_stores_once_and_preserves_its_result()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var target = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "target",
            SourceTypeIndex.Invalid,
            unknown);
        var targetReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: target);
        var seven = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 7));
        var assignmentOrigin = builder.AddOrigin(0, 2, 7, 17);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            origin: assignmentOrigin,
            children: [targetReference, seven]);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: assignment);
        var method = CreateMethod(builder, [returnStatement]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.PushDuplicate,
                ActionOpcode.Push,
                ActionOpcode.StackSwap,
                ActionOpcode.SetVariable,
                ActionOpcode.Return
            ]);
        GetPushedString(artifact.ActionBody.Actions[2]).ShouldBe("target");
        artifact.InstructionMap
            .Where(mapping => mapping.Origin == assignmentOrigin)
            .Count().ShouldBe(4);
        var roundTrip = DecompileBack(artifact);
        roundTrip.ShouldContain("target = 7;");
        roundTrip.ShouldContain("return 7;");
    }

    [Fact]
    public void Valueless_return_pushes_undefined_for_action_return()
    {
        var builder = new Avm1SourceArena.Builder();
        var returnStatement = builder.AddStatement(Avm1SourceStatementKind.Return);
        var artifact = Compile(CreateMethod(builder, [returnStatement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var push = artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionPush>();
        push.PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueUndefined>();
        artifact.ActionBody.Actions[1].ShouldBeOfType<ActionReturn>();
    }

    [Fact]
    public void Empty_declaration_and_expression_statement_leave_an_empty_stack()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var local = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "value",
            SourceTypeIndex.Invalid,
            unknown);
        var declaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            symbol: local);
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1));
        var expression = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: literal);

        var artifact = Compile(CreateMethod(builder, [declaration, expression]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.DefineLocal2,
                ActionOpcode.Push,
                ActionOpcode.Pop
            ]);
        artifact.StackLir.ShouldNotBeNull().MaximumStackDepth.ShouldBe(1);
        artifact.ActionBody.MaximumStackDepth.ShouldBe(1);
    }

    [Fact]
    public void Selects_supported_unary_actions()
    {
        (Avm1SourceOperator Operator, ActionOpcode Opcode)[] cases =
        [
            (Avm1SourceOperator.LogicalNot, ActionOpcode.Not),
            (Avm1SourceOperator.TypeOf, ActionOpcode.TypeOf)
        ];

        foreach (var (@operator, opcode) in cases)
        {
            var artifact = Compile(CreateUnaryReturn(@operator));

            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            artifact.ActionBody.ShouldNotBeNull().Actions
                .Select(action => action.Opcode).ShouldBe([
                    ActionOpcode.Push,
                    opcode,
                    ActionOpcode.Return
                ]);
        }
    }

    [Fact]
    public void Lowers_source_intrinsics_to_direct_avm1_actions()
    {
        (string Name, ActionOpcode Opcode, bool HasArgument)[] cases =
        [
            ("getTimer", ActionOpcode.GetTime, false),
            ("targetPath", ActionOpcode.TargetPath, true),
            ("Number", ActionOpcode.ToNumber, true),
            ("String", ActionOpcode.ToString, true)
        ];

        foreach (var (name, opcode, hasArgument) in cases)
        {
            var builder = new Avm1SourceArena.Builder();
            var arguments = hasArgument
                ? new[]
                {
                    builder.AddExpression(
                        Avm1SourceExpressionKind.Literal,
                        literal: builder.AddLiteral(
                            Avm1SourceLiteralKind.Integer,
                            integerValue: 7))
                }
                : [];
            var intrinsic = builder.AddExpression(
                Avm1SourceExpressionKind.IntrinsicCall,
                name: builder.InternString(name),
                children: arguments);
            var method = CreateMethod(builder, [
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: intrinsic)
            ]);

            var artifact = Compile(method);

            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            artifact.ActionBody.ShouldNotBeNull().Actions
                .Select(action => action.Opcode).ShouldBe(hasArgument
                    ? [ActionOpcode.Push, opcode, ActionOpcode.Return]
                    : [opcode, ActionOpcode.Return]);
        }
    }

    [Fact]
    public void Trace_is_observable_and_yields_undefined_when_used_as_a_value()
    {
        var builder = new Avm1SourceArena.Builder();
        var argument = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("message")));
        var trace = builder.AddExpression(
            Avm1SourceExpressionKind.IntrinsicCall,
            name: builder.InternString("trace"),
            children: [argument]);
        var method = CreateMethod(builder, [
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: trace)
        ]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Trace).Effects.ShouldBe(
                Avm1MirEffect.WritesRuntimeState |
                Avm1MirEffect.MayInvokeUserCode |
                Avm1MirEffect.MayThrow);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Trace,
                ActionOpcode.Push,
                ActionOpcode.Return
            ]);
        artifact.ActionBody.Actions[2].ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueUndefined>();
    }

    [Fact]
    public void Timeline_intrinsic_is_observable_and_yields_undefined_as_a_value()
    {
        var builder = new Avm1SourceArena.Builder();
        var play = builder.AddExpression(
            Avm1SourceExpressionKind.IntrinsicCall,
            name: builder.InternString("play"));
        var method = CreateMethod(builder, [
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: play)
        ]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.TimelinePlay)
            .Effects.ShouldBe(Avm1MirEffect.WritesRuntimeState);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Play,
                ActionOpcode.Push,
                ActionOpcode.Return
            ]);
        artifact.ActionBody.Actions[1].ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueUndefined>();
    }

    [Fact]
    public void Timeline_goto_intrinsic_consumes_its_frame_and_yields_undefined_as_a_value()
    {
        var builder = new Avm1SourceArena.Builder();
        var frame = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("label")));
        var gotoAndStop = builder.AddExpression(
            Avm1SourceExpressionKind.IntrinsicCall,
            name: builder.InternString("gotoAndStop"),
            children: [frame]);
        var method = CreateMethod(builder, [
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: gotoAndStop)
        ]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is
                Avm1MirInstructionKind.TimelineGotoImmediateAndStop)
            .Effects.ShouldBe(Avm1MirEffect.WritesRuntimeState);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.GoToLabel,
                ActionOpcode.Push,
                ActionOpcode.Return
            ]);
        artifact.ActionBody.Actions[0].ShouldBeOfType<ActionGoToLabel>()
            .Label.ShouldBe("label");
        artifact.ActionBody.Actions[1].ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueUndefined>();
    }

    [Fact]
    public void Fscommand_intrinsic_is_observable_and_yields_undefined_as_a_value()
    {
        var builder = new Avm1SourceArena.Builder();
        var command = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("quit")));
        var fscommand = builder.AddExpression(
            Avm1SourceExpressionKind.IntrinsicCall,
            name: builder.InternString("fscommand"),
            children: [command]);
        var method = CreateMethod(builder, [
            builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: fscommand)
        ]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.GetUrlImmediate)
            .Effects.ShouldBe(
                Avm1MirEffect.WritesRuntimeState |
                Avm1MirEffect.MayInvokeUserCode |
                Avm1MirEffect.MayThrow);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.GetURL,
                ActionOpcode.Push,
                ActionOpcode.Return
            ]);
        var request = artifact.ActionBody.Actions[0]
            .ShouldBeOfType<ActionGetURL>();
        request.Url.ShouldBe("FSCommand:quit");
        request.Target.ShouldBeEmpty();
        artifact.ActionBody.Actions[1].ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueUndefined>();
    }

    [Fact]
    public void Compiled_assignment_statement_has_observable_machine_semantics()
    {
        var builder = new Avm1SourceArena.Builder();
        var target = builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString("target"));
        var value = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 7));
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [target, value]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
        machine.Globals["target"].AsNumber.ShouldBe(7d);
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.StoreLValue).Effects.ShouldBe(
                Avm1MirEffect.WritesDynamicScope |
                Avm1MirEffect.MayInvokeUserCode |
                Avm1MirEffect.MayThrow);
    }

    [Fact]
    public void Lowers_every_source_literal_to_an_exact_push_form()
    {
        (Avm1SourceLiteralKind Kind, object? Value, Type PushType)[] cases =
        [
            (Avm1SourceLiteralKind.Undefined, null, typeof(PushValue.PushValueUndefined)),
            (Avm1SourceLiteralKind.Null, null, typeof(PushValue.PushValueNull)),
            (Avm1SourceLiteralKind.Boolean, true, typeof(PushValue.PushValueBoolean)),
            (Avm1SourceLiteralKind.Integer, 12, typeof(PushValue.PushValueInteger)),
            (Avm1SourceLiteralKind.Number, 1.25, typeof(PushValue.PushValueDouble)),
            (Avm1SourceLiteralKind.String, "text", typeof(PushValue.PushValueString))
        ];

        foreach (var (kind, value, pushType) in cases)
        {
            var method = CreateLiteralReturn(kind, value);
            var artifact = Compile(method);

            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            var pushValue = artifact.ActionBody.ShouldNotBeNull().Actions[0]
                .ShouldBeOfType<ActionPush>().PushValues.ShouldHaveSingleItem();
            pushValue.GetType().ShouldBe(pushType);
        }
    }

    [Fact]
    public void Selects_direct_and_negated_binary_actions_without_reordering_operands()
    {
        (Avm1SourceOperator Operator, ActionOpcode Opcode, bool Negate)[] cases =
        [
            (Avm1SourceOperator.Add, ActionOpcode.Add2, false),
            (Avm1SourceOperator.Subtract, ActionOpcode.Subtract, false),
            (Avm1SourceOperator.Multiply, ActionOpcode.Multiply, false),
            (Avm1SourceOperator.Divide, ActionOpcode.Divide, false),
            (Avm1SourceOperator.Modulo, ActionOpcode.Modulo, false),
            (Avm1SourceOperator.Equal, ActionOpcode.Equals2, false),
            (Avm1SourceOperator.NotEqual, ActionOpcode.Equals2, true),
            (Avm1SourceOperator.StrictEqual, ActionOpcode.StrictEquals, false),
            (Avm1SourceOperator.StrictNotEqual, ActionOpcode.StrictEquals, true),
            (Avm1SourceOperator.Less, ActionOpcode.Less2, false),
            (Avm1SourceOperator.LessOrEqual, ActionOpcode.Greater, true),
            (Avm1SourceOperator.Greater, ActionOpcode.Greater, false),
            (Avm1SourceOperator.GreaterOrEqual, ActionOpcode.Less2, true),
            (Avm1SourceOperator.BitAnd, ActionOpcode.BitAnd, false),
            (Avm1SourceOperator.BitOr, ActionOpcode.BitOr, false),
            (Avm1SourceOperator.BitXor, ActionOpcode.BitXor, false),
            (Avm1SourceOperator.ShiftLeft, ActionOpcode.BitLShift, false),
            (Avm1SourceOperator.ShiftRight, ActionOpcode.BitRShift, false),
            (Avm1SourceOperator.ShiftRightUnsigned, ActionOpcode.BitURShift, false),
            (Avm1SourceOperator.InstanceOf, ActionOpcode.InstanceOf, false)
        ];

        foreach (var (@operator, opcode, negate) in cases)
        {
            var artifact = Compile(CreateBinaryReturn(@operator));
            artifact.Succeeded.ShouldBeTrue(
                $"{@operator}:{Environment.NewLine}{Describe(artifact)}");
            var opcodes = artifact.ActionBody.ShouldNotBeNull().Actions
                .Select(action => action.Opcode)
                .ToArray();
            opcodes[0].ShouldBe(ActionOpcode.Push);
            opcodes[1].ShouldBe(ActionOpcode.Push);
            opcodes[2].ShouldBe(opcode);
            opcodes.Skip(3).ShouldBe(negate
                ? [ActionOpcode.Not, ActionOpcode.Return]
                : [ActionOpcode.Return]);
        }
    }

    [Fact]
    public void Basic_optimization_folds_a_primitive_ssa_tree()
    {
        var builder = new Avm1SourceArena.Builder();
        var sum = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Add,
            AddIntegerLiteral(builder, 1),
            AddIntegerLiteral(builder, 2));
        var product = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Multiply,
            sum,
            AddIntegerLiteral(builder, 3));
        var method = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: product)]);

        var unoptimized = Compile(
            method,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.None
            });
        var optimized = Compile(
            method,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        unoptimized.Succeeded.ShouldBeTrue(Describe(unoptimized));
        optimized.Succeeded.ShouldBeTrue(Describe(optimized));
        unoptimized.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Binary).ShouldBe(2);
        optimized.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldBe([
                Avm1MirInstructionKind.Constant,
                Avm1MirInstructionKind.Return
            ]);
        optimized.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Return
            ]);
        GetPushedValue(optimized.ActionBody.Actions[0])
            .ShouldBe(PushValue.Integer(9));

        var unoptimizedMachine = new Avm1Machine(swfVersion: 7);
        var optimizedMachine = new Avm1Machine(swfVersion: 7);
        unoptimizedMachine.Execute(
            unoptimized.ActionBody.ShouldNotBeNull().Actions,
            strict: true);
        optimizedMachine.Execute(optimized.ActionBody.Actions, strict: true);
        optimizedMachine.ReturnValue.AsNumber.ShouldBe(
            unoptimizedMachine.ReturnValue.AsNumber);
        optimizedMachine.ReturnValue.AsNumber.ShouldBe(9d);
    }

    [Fact]
    public void Basic_optimization_removes_only_a_discarded_pure_tree()
    {
        var builder = new Avm1SourceArena.Builder();
        var pure = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddIntegerLiteral(builder, 1),
                AddIntegerLiteral(builder, 2)));
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "touch")]);
        var effectful = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: call);

        var artifact = Compile(
            CreateMethod(builder, [pure, effectful]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Constant ||
            instruction.Kind == Avm1MirInstructionKind.Binary);
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Invoke).ShouldBe(1);
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Discard).ShouldBe(1);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.CallFunction);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.Add2);
    }

    [Fact]
    public void Basic_optimization_preserves_dynamic_binary_conversion()
    {
        var builder = new Avm1SourceArena.Builder();
        var expression = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Add,
            AddDynamicName(builder, "value"),
            AddIntegerLiteral(builder, 1));
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: expression)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Binary &&
            instruction.Effects.HasFlag(Avm1MirEffect.MayInvokeUserCode));
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.Add2);
    }

    [Fact]
    public void Basic_optimization_preserves_primitive_edge_semantics()
    {
        var nanBuilder = new Avm1SourceArena.Builder();
        var nanComparison = AddBinaryExpression(
            nanBuilder,
            Avm1SourceOperator.StrictEqual,
            AddNumberLiteral(nanBuilder, double.NaN),
            AddNumberLiteral(nanBuilder, double.NaN));
        var nanArtifact = Compile(
            CreateMethod(
                nanBuilder,
                [nanBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: nanComparison)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        nanArtifact.Succeeded.ShouldBeTrue(Describe(nanArtifact));
        GetPushedValue(nanArtifact.ActionBody.ShouldNotBeNull().Actions[0])
            .ShouldBe(PushValue.Boolean(false));

        var zeroBuilder = new Avm1SourceArena.Builder();
        var negativeZero = AddBinaryExpression(
            zeroBuilder,
            Avm1SourceOperator.Multiply,
            AddNumberLiteral(zeroBuilder, -0d),
            AddIntegerLiteral(zeroBuilder, 1));
        var zeroArtifact = Compile(
            CreateMethod(
                zeroBuilder,
                [zeroBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: negativeZero)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        zeroArtifact.Succeeded.ShouldBeTrue(Describe(zeroArtifact));
        var zero = GetPushedValue(zeroArtifact.ActionBody.ShouldNotBeNull().Actions[0])
            .ShouldBeOfType<PushValue.PushValueDouble>().Value;
        BitConverter.DoubleToInt64Bits(zero).ShouldBe(long.MinValue);

        var shiftBuilder = new Avm1SourceArena.Builder();
        var unsignedShift = AddBinaryExpression(
            shiftBuilder,
            Avm1SourceOperator.ShiftRightUnsigned,
            AddIntegerLiteral(shiftBuilder, -1),
            AddIntegerLiteral(shiftBuilder, 0));
        var shiftArtifact = Compile(
            CreateMethod(
                shiftBuilder,
                [shiftBuilder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: unsignedShift)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        shiftArtifact.Succeeded.ShouldBeTrue(Describe(shiftArtifact));
        GetPushedValue(shiftArtifact.ActionBody.ShouldNotBeNull().Actions[0])
            .ShouldBe(PushValue.Double(uint.MaxValue));
    }

    [Fact]
    public void Basic_optimization_does_not_raise_the_required_swf_version()
    {
        var builder = new Avm1SourceArena.Builder();
        var logicalNot = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.LogicalNot,
            children: [AddStringLiteral(builder, "value")]);
        var emptyBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: []);
        var condition = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: logicalNot,
            children: [emptyBlock]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [condition]),
            new Avm1CompilationOptions(4)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Unary ||
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.Not ||
            action.Opcode == ActionOpcode.If);
    }

    [Fact]
    public void Basic_optimization_propagates_through_an_executable_phi_edge()
    {
        var builder = new Avm1SourceArena.Builder();
        var unreachableCall = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "unreachable")]);
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddBooleanLiteral(builder, true),
                AddIntegerLiteral(builder, 2),
                unreachableCall
            ]);
        var sum = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Add,
            conditional,
            AddIntegerLiteral(builder, 3));
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: sum)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldBeEmpty();
        artifact.Mir.Blocks.ShouldContain(block =>
            !block.IsReachable && block.Instructions.Count == 0);
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Invoke ||
            instruction.Kind == Avm1MirInstructionKind.Binary ||
            instruction.Kind == Avm1MirInstructionKind.StorePhi ||
            instruction.Kind == Avm1MirInstructionKind.Phi);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.CallFunction ||
            action.Opcode == ActionOpcode.Add2);
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(5d);
    }

    [Fact]
    public void Basic_optimization_removes_a_constant_short_circuit_rhs()
    {
        var builder = new Avm1SourceArena.Builder();
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "touch")]);
        var logical = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children: [AddBooleanLiteral(builder, false), call]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: logical)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldBeEmpty();
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Invoke ||
            instruction.Kind == Avm1MirInstructionKind.StorePhi ||
            instruction.Kind == Avm1MirInstructionKind.Phi);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.CallFunction);
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsBoolean.ShouldBeFalse();
    }

    [Fact]
    public void Basic_optimization_eliminates_a_dynamic_single_edge_phi()
    {
        var builder = new Avm1SourceArena.Builder();
        var unreachableCall = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "unreachable")]);
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddBooleanLiteral(builder, true),
                AddDynamicName(builder, "value"),
                unreachableCall
            ]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: conditional)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldBeEmpty();
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Branch ||
            instruction.Kind == Avm1MirInstructionKind.Invoke ||
            instruction.Kind == Avm1MirInstructionKind.StorePhi ||
            instruction.Kind == Avm1MirInstructionKind.Phi);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.Jump ||
            action.Opcode == ActionOpcode.CallFunction ||
            action.Opcode == ActionOpcode.StoreRegister);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1Number(42);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(42d);
    }

    [Fact]
    public void Basic_optimization_eliminates_the_short_circuit_phi_copy()
    {
        var builder = new Avm1SourceArena.Builder();
        var logical = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children:
            [
                AddBooleanLiteral(builder, true),
                AddDynamicName(builder, "value")
            ]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: logical)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldBeEmpty();
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Branch ||
            instruction.Kind == Avm1MirInstructionKind.StorePhi ||
            instruction.Kind == Avm1MirInstructionKind.Phi);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.Jump ||
            action.Opcode == ActionOpcode.StoreRegister);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1Number(9);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(9d);
    }

    [Fact]
    public void Basic_optimization_eliminates_chained_single_edge_phis()
    {
        var builder = new Avm1SourceArena.Builder();
        var inner = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddBooleanLiteral(builder, true),
                AddDynamicName(builder, "value"),
                builder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children: [AddDynamicName(builder, "innerUnreachable")])
            ]);
        var outer = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddBooleanLiteral(builder, true),
                inner,
                builder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children: [AddDynamicName(builder, "outerUnreachable")])
            ]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: outer)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldBeEmpty();
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Branch ||
            instruction.Kind == Avm1MirInstructionKind.Invoke ||
            instruction.Kind == Avm1MirInstructionKind.StorePhi ||
            instruction.Kind == Avm1MirInstructionKind.Phi);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.Jump ||
            action.Opcode == ActionOpcode.CallFunction ||
            action.Opcode == ActionOpcode.StoreRegister);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1Number(17);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(17d);
    }

    [Fact]
    public void Basic_optimization_eliminates_a_phi_before_implicit_exit()
    {
        var builder = new Avm1SourceArena.Builder();
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddBooleanLiteral(builder, true),
                AddDynamicName(builder, "value"),
                builder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children: [AddDynamicName(builder, "unreachable")])
            ]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: conditional)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldBeEmpty();
        var implicitExit = artifact.Mir.Blocks.Single(block =>
            block.IsReachable && block.Instructions.Count == 0);
        artifact.Mir.BlockLayout[^1].ShouldBe(implicitExit.Index);
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Invoke ||
            instruction.Kind == Avm1MirInstructionKind.StorePhi ||
            instruction.Kind == Avm1MirInstructionKind.Phi);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.Jump ||
            action.Opcode == ActionOpcode.CallFunction ||
            action.Opcode == ActionOpcode.StoreRegister);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1Number(23);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
    }

    [Fact]
    public void Basic_optimization_coalesces_an_adjacent_temporary_copy()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(builder, "value")]);
        var source = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: increment)]);
        var options = CreateFunction2Options(
            registerCount: 1,
            temporaryCount: 1) with
        {
            OptimizationLevel = Avm1OptimizationLevel.Basic
        };

        var artifact = Compile(source, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.LoadTemporary).ShouldBe(1);
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.StoreTemporary)
            .Result.IsValid.ShouldBeTrue();
        artifact.StackLir.ShouldNotBeNull().Instructions.Count(instruction =>
            instruction.Kind is Avm1StackLirInstructionKind.PushTemporary)
            .ShouldBe(1);
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action is ActionStoreRegister).ShouldBe(1);
        artifact.ActionBody.Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .Count(value => value is PushValue.PushValueRegister)
            .ShouldBe(1);

        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1String("3");

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.Globals["value"].AsNumber.ShouldBe(4d);
        machine.ReturnValue.AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void Basic_optimization_emits_a_profitable_deterministic_constant_pool()
    {
        const string name = "very_long_dynamic_name_for_constant_pooling";
        var source = CreateRepeatedDynamicReadMethod(
            name,
            occurrenceCount: 4,
            returnLast: true);
        var options = new Avm1CompilationOptions(7)
        {
            OptimizationLevel = Avm1OptimizationLevel.Basic
        };

        var baseline = Compile(source);
        var artifact = Compile(source, options);
        var repeated = Compile(source, options);

        baseline.Succeeded.ShouldBeTrue(Describe(baseline));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        repeated.Succeeded.ShouldBeTrue(Describe(repeated));
        artifact.Bytecode.ToArray().ShouldBe(repeated.Bytecode.ToArray());
        artifact.Bytecode.Length.ShouldBeLessThan(baseline.Bytecode.Length);
        artifact.ActionBody.ShouldNotBeNull().Actions[0]
            .ShouldBeOfType<ActionConstantPool>().Constants.ShouldBe([name]);
        artifact.ActionBody.Actions.OfType<ActionConstantPool>()
            .ShouldHaveSingleItem();
        artifact.ActionBody.Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .Count(value => value is PushValue.PushValueConstant8)
            .ShouldBe(4);

        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals[name] = new Avm1Number(19);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(19d);
    }

    [Fact]
    public void Basic_optimization_skips_an_unprofitable_constant_pool()
    {
        var artifact = Compile(
            CreateRepeatedDynamicReadMethod(
                "x",
                occurrenceCount: 1,
                returnLast: true),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action is ActionConstantPool);
        artifact.ActionBody.Actions.OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .ShouldContain(value => value is PushValue.PushValueString);
    }

    [Fact]
    public void Basic_optimization_does_not_emit_a_constant_pool_before_swf5()
    {
        const string name = "very_long_dynamic_name_for_swf4";
        var artifact = Compile(
            CreateRepeatedDynamicReadMethod(
                name,
                occurrenceCount: 4,
                returnLast: false),
            new Avm1CompilationOptions(4)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action is ActionConstantPool);
        artifact.ActionBody.Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .Count(value => value is PushValue.PushValueString)
            .ShouldBe(4);
    }

    [Fact]
    public void Basic_optimization_uses_constant16_after_256_pool_entries()
    {
        var builder = new Avm1SourceArena.Builder();
        var statements = new List<SourceStatementIndex>();
        var names = Enumerable.Range(0, 257)
            .Select(index =>
                $"pooled_dynamic_name_{index:D3}_with_a_long_suffix")
            .ToArray();
        foreach (var name in names)
        {
            for (var occurrence = 0; occurrence < 3; occurrence++)
            {
                statements.Add(builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: AddDynamicName(builder, name)));
            }
        }
        var artifact = Compile(
            CreateMethod(builder, statements),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var pool = artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionConstantPool>()
            .ShouldHaveSingleItem();
        pool.Constants.Count.ShouldBe(257);
        pool.Constants[0].ShouldBe(names[0]);
        pool.Constants[256].ShouldBe(names[256]);
        var pushedValues = artifact.ActionBody.Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .ToArray();
        pushedValues.ShouldContain(value =>
            value is PushValue.PushValueConstant8);
        pushedValues.ShouldContain(value =>
            value is PushValue.PushValueConstant16);
    }

    [Fact]
    public void Basic_optimization_packs_compatible_pushes_and_preserves_source_map()
    {
        var builder = new Avm1SourceArena.Builder();
        var callOrigin = builder.AddOrigin(0, 0, 7, 12);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "fn")],
            origin: callOrigin);
        var source = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: call)]);
        var options = new Avm1CompilationOptions(7)
        {
            OptimizationLevel = Avm1OptimizationLevel.Basic
        };

        var baseline = Compile(source);
        var artifact = Compile(source, options);

        baseline.Succeeded.ShouldBeTrue(Describe(baseline));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Bytecode.Length.ShouldBe(baseline.Bytecode.Length - 3);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.CallFunction,
                ActionOpcode.Return
            ]);
        artifact.ActionBody.Actions[0].ShouldBeOfType<ActionPush>()
            .PushValues.ShouldBe([
                PushValue.Integer(0),
                PushValue.String("fn")
            ]);

        var invocation = artifact.Mir.Instructions
            .Where(instruction =>
                instruction.Kind is Avm1MirInstructionKind.Invoke)
            .ShouldHaveSingleItem();
        var stackLir = artifact.StackLir.ShouldNotBeNull();
        var packedMappings = artifact.InstructionMap
            .Where(mapping =>
                mapping.MirInstruction == invocation.Index &&
                stackLir[mapping.StackInstruction].Kind is
                    Avm1StackLirInstructionKind.PushConstant or
                    Avm1StackLirInstructionKind.PushName)
            .ToArray();
        packedMappings.Length.ShouldBe(2);
        packedMappings
            .Select(mapping => mapping.AssemblyInstruction)
            .Distinct()
            .ShouldHaveSingleItem();
        artifact.SourceMap.GetEntries(callOrigin)
            .Select(entry => artifact.ActionBody.Actions[entry.ActionIndex].Opcode)
            .ShouldBe([ActionOpcode.Push, ActionOpcode.CallFunction]);
        DecompileBack(artifact).ShouldContain("return fn();");
    }

    [Fact]
    public void Basic_push_packing_respects_the_action_payload_limit()
    {
        var builder = new Avm1SourceArena.Builder();
        var name = new string('x', ushort.MaxValue - 2);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, name)]);
        var source = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: call)]);

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.CallFunction,
                ActionOpcode.Return
            ]);
        var pushes = artifact.ActionBody.Actions.OfType<ActionPush>().ToArray();
        pushes.Length.ShouldBe(2);
        pushes.ShouldAllBe(push => push.PushValues.Count == 1);
        pushes[1].PushValues.ShouldHaveSingleItem()
            .ShouldBe(PushValue.String(name));
    }

    [Fact]
    public void Basic_constant_pool_uses_profitable_block_segments()
    {
        const int namesPerArm = 150;
        const int occurrencesPerName = 3;
        const int nameLength = 210;
        var builder = new Avm1SourceArena.Builder();
        var thenStatements = new List<SourceStatementIndex>();
        var elseStatements = new List<SourceStatementIndex>();
        var thenNames = CreateNames("then");
        var elseNames = CreateNames("else");
        AddReads(thenStatements, thenNames);
        AddReads(elseStatements, elseNames);
        var conditional = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "condition"),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: thenStatements),
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: elseStatements)
            ]);
        var source = CreateMethod(builder, [conditional]);
        var options = new Avm1CompilationOptions(7)
        {
            OptimizationLevel = Avm1OptimizationLevel.Basic
        };

        var artifact = Compile(source, options);
        var repeated = Compile(source, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        repeated.Succeeded.ShouldBeTrue(Describe(repeated));
        artifact.Bytecode.ToArray().ShouldBe(repeated.Bytecode.ToArray());
        var pools = artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionConstantPool>()
            .ToArray();
        pools.Length.ShouldBe(2);
        pools.ShouldAllBe(pool => pool.Constants.Count == namesPerArm);
        pools[0].Constants.Intersect(
            pools[1].Constants,
            StringComparer.Ordinal).ShouldBeEmpty();
        var pushedValues = artifact.ActionBody.Actions
            .OfType<ActionPush>()
            .SelectMany(action => action.PushValues)
            .ToArray();
        pushedValues.Count(value =>
            value is PushValue.PushValueConstant8).ShouldBe(
                namesPerArm * occurrencesPerName * 2);
        pushedValues.ShouldNotContain(value =>
            value is PushValue.PushValueConstant16);

        foreach (var condition in new[] { 0, 1 })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(condition);

            machine.Execute(artifact.ActionBody.Actions, strict: true);

            machine.UnsupportedOpcodes.ShouldBeEmpty();
        }

        string[] CreateNames(string arm) =>
            Enumerable.Range(0, namesPerArm)
                .Select(index =>
                {
                    var prefix = $"segmented_pool_{arm}_{index:D3}_";
                    return prefix + new string('x', nameLength - prefix.Length);
                })
                .ToArray();

        void AddReads(
            List<SourceStatementIndex> statements,
            IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                for (var occurrence = 0;
                    occurrence < occurrencesPerName;
                    occurrence++)
                {
                    statements.Add(builder.AddStatement(
                        Avm1SourceStatementKind.Expression,
                        expression: AddDynamicName(builder, name)));
                }
            }
        }
    }

    [Fact]
    public void Basic_optimization_coalesces_a_constant_if_chain()
    {
        var builder = new Avm1SourceArena.Builder();
        var thenBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 7)]);
        var condition = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBooleanLiteral(builder, true),
            children: [thenBlock]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [
                    condition,
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: AddDynamicName(builder, "result"))
                ]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Branch);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.Jump);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["result"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(7d);
    }

    [Fact]
    public void Basic_optimization_keeps_a_shared_dynamic_if_merge()
    {
        var builder = new Avm1SourceArena.Builder();
        var whenTrue = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 1)]);
        var whenFalse = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 2)]);
        var condition = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "condition"),
            children: [whenTrue, whenFalse]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [
                    condition,
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: AddDynamicName(builder, "result"))
                ]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue);
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Branch).ShouldBe(2);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.If);
        artifact.ActionBody.Actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.Jump);

        foreach (var testCase in new[]
        {
            (Condition: 0, Expected: 2),
            (Condition: 1, Expected: 1)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(testCase.Condition);
            machine.Globals["result"] = new Avm1Number(0);

            machine.Execute(artifact.ActionBody.Actions, strict: true);

            machine.ReturnValue.AsNumber.ShouldBe(testCase.Expected);
        }
    }

    [Fact]
    public void Basic_optimization_keeps_both_dynamic_conditional_edges()
    {
        var builder = new Avm1SourceArena.Builder();
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(builder, "condition"),
                AddIntegerLiteral(builder, 1),
                AddIntegerLiteral(builder, 2)
            ]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: conditional)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldHaveSingleItem().Incomings.Count.ShouldBe(2);
        artifact.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.If);
    }

    [Fact]
    public void Basic_optimization_remaps_phi_predecessor_after_arm_coalescing()
    {
        var builder = new Avm1SourceArena.Builder();
        var selectedArm = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddBooleanLiteral(builder, true),
                AddDynamicName(builder, "left"),
                builder.AddExpression(
                    Avm1SourceExpressionKind.Call,
                    children: [AddDynamicName(builder, "unreachable")])
            ]);
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(builder, "condition"),
                selectedArm,
                AddDynamicName(builder, "right")
            ]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: conditional)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldHaveSingleItem().Incomings.Count.ShouldBe(2);
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue).ShouldBe(1);
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Invoke);
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action.Opcode == ActionOpcode.If).ShouldBe(1);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.CallFunction);

        foreach (var testCase in new[]
        {
            (Condition: 0, Expected: 22),
            (Condition: 1, Expected: 11)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(testCase.Condition);
            machine.Globals["left"] = new Avm1Number(11);
            machine.Globals["right"] = new Avm1Number(22);

            machine.Execute(artifact.ActionBody.Actions, strict: true);

            machine.ReturnValue.AsNumber.ShouldBe(testCase.Expected);
        }
    }

    [Fact]
    public void Basic_optimization_meets_equal_constants_from_both_phi_edges()
    {
        var builder = new Avm1SourceArena.Builder();
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(builder, "condition"),
                AddIntegerLiteral(builder, 2),
                AddIntegerLiteral(builder, 2)
            ]);
        var sum = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Add,
            conditional,
            AddIntegerLiteral(builder, 3));
        var artifact = Compile(
            CreateMethod(
                builder,
                [builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: sum)]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldHaveSingleItem().Incomings.Count.ShouldBe(2);
        artifact.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue);
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Binary);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.If);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.Add2);

        foreach (var condition in new[] { 0, 1 })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(condition);

            machine.Execute(artifact.ActionBody.Actions, strict: true);

            machine.ReturnValue.AsNumber.ShouldBe(5d);
        }
    }

    [Fact]
    public void Basic_optimization_removes_a_constant_false_loop_body()
    {
        var builder = new Avm1SourceArena.Builder();
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "touch")]);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: call);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddBooleanLiteral(builder, false),
            children: [body]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [
                    loop,
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: AddIntegerLiteral(builder, 7))
                ]),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.ShouldNotContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.BranchIfTrue ||
            instruction.Kind == Avm1MirInstructionKind.Invoke);
        artifact.Mir.Blocks.Count(block => !block.IsReachable)
            .ShouldBeGreaterThanOrEqualTo(1);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldNotContain(action =>
            action.Opcode == ActionOpcode.If ||
            action.Opcode == ActionOpcode.CallFunction);
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(7d);
    }

    [Fact]
    public void Basic_optimization_does_not_hide_an_unsupported_operation()
    {
        var builder = new Avm1SourceArena.Builder();
        var add = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Add,
            AddStringLiteral(builder, "left"),
            AddStringLiteral(builder, "right"));
        var emptyBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: []);
        var condition = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: add,
            children: [emptyBlock]);

        var artifact = Compile(
            CreateMethod(builder, [condition]),
            new Avm1CompilationOptions(4)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeFalse();
        artifact.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Binary);
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains("Add2", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("SWF 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_unknown_optimization_level()
    {
        var artifact = Compile(
            CreateLiteralReturn(Avm1SourceLiteralKind.Integer, 1),
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = (Avm1OptimizationLevel)byte.MaxValue
            });

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP010" &&
            diagnostic.Message.Contains(
                "Optimization level",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_unknown_source_register_allocation_mode()
    {
        var artifact = Compile(
            CreateLiteralReturn(Avm1SourceLiteralKind.Integer, 1),
            new Avm1CompilationOptions(7)
            {
                SourceRegisterAllocationMode =
                    (Avm1SourceRegisterAllocationMode)byte.MaxValue
            });

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP010" &&
            diagnostic.Message.Contains(
                "Source-register allocation mode",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Short_circuit_join_spills_when_the_private_register_file_is_empty()
    {
        var artifact = Compile(
            CreateBinaryReturn(Avm1SourceOperator.LogicalAnd),
            CreateFunction2Options(registerCount: 0, temporaryCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        AssertActivationSpills(artifact, expectedCount: 1);
        artifact.ActionBody.ShouldNotBeNull().MaximumStackDepth.ShouldBe(
            artifact.StackLir.ShouldNotBeNull().MaximumStackDepth);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action is ActionDefineLocal);
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(2d);
    }

    [Fact]
    public void Legacy_code_units_spill_temporaries_beyond_the_fixed_register_file()
    {
        var builder = new Avm1SourceArena.Builder();
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "fn"),
                AddIntegerLiteral(builder, 1),
                AddIntegerLiteral(builder, 2),
                AddIntegerLiteral(builder, 3),
                AddIntegerLiteral(builder, 4),
                AddIntegerLiteral(builder, 5)
            ]);
        var method = CreateMethod(builder, [
            builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: call)
        ]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([
            (byte)0,
            (byte)1,
            (byte)2,
            (byte)3
        ]);
        AssertActivationSpills(artifact, expectedCount: 1);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action is ActionDefineLocal);
    }

    [Theory]
    [InlineData(Avm1SourceOperator.LogicalAnd, 0, 0, 0)]
    [InlineData(Avm1SourceOperator.LogicalAnd, 5, 1, 1)]
    [InlineData(Avm1SourceOperator.LogicalOr, 5, 0, 5)]
    [InlineData(Avm1SourceOperator.LogicalOr, 0, 1, 1)]
    public void Short_circuit_operators_preserve_operand_values_and_rhs_effects(
        Avm1SourceOperator @operator,
        int leftValue,
        int expectedCount,
        int expectedResult)
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)
            ]);
        var right = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "count"), increment]);
        var logical = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            @operator,
            children: [AddDynamicName(builder, "left"), right]);
        var result = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), logical]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: result);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldHaveSingleItem().Incomings.Count.ShouldBe(2);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .ShouldContain(ActionOpcode.If);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["left"] = new Avm1Number(leftValue);
        machine.Globals["count"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(expectedCount);
        machine.Globals["result"].AsNumber.ShouldBe(expectedResult);
    }

    [Theory]
    [InlineData(0, 22)]
    [InlineData(1, 11)]
    public void Conditional_expression_evaluates_only_the_selected_arm(
        int condition,
        int expected)
    {
        var builder = new Avm1SourceArena.Builder();
        var whenTrue = AddAssignmentExpression(builder, "selected", 11);
        var whenFalse = AddAssignmentExpression(builder, "selected", 22);
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(builder, "condition"),
                whenTrue,
                whenFalse
            ]);
        var result = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), conditional]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: result);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.ShouldHaveSingleItem().Incomings.Count.ShouldBe(2);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["condition"] = new Avm1Number(condition);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["selected"].AsNumber.ShouldBe(expected);
        machine.Globals["result"].AsNumber.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, 109)]
    [InlineData(1, 107)]
    public void Conditional_inside_binary_spills_and_restores_the_outer_value(
        int condition,
        int expected)
    {
        var method = CreateOuterValueConditional();
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);

        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.Count.ShouldBe(2);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["condition"] = new Avm1Number(condition);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["result"].AsNumber.ShouldBe(expected);
    }

    [Fact]
    public void Outer_live_value_spills_excess_stack_carry_capacity()
    {
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);

        var artifact = Compile(CreateOuterValueConditional(), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
        AssertActivationSpills(artifact, expectedCount: 1);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["condition"] = new Avm1Number(1);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["result"].AsNumber.ShouldBe(107d);
    }

    [Theory]
    [InlineData(0, 0, 7, 0)]
    [InlineData(1, 4, 7, 4)]
    [InlineData(1, 0, 7, 7)]
    public void Nested_short_circuit_joins_keep_independent_phi_temporaries(
        int left,
        int middle,
        int right,
        int expected)
    {
        var builder = new Avm1SourceArena.Builder();
        var inner = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalOr,
            children:
            [
                AddDynamicName(builder, "middle"),
                AddDynamicName(builder, "right")
            ]);
        var outer = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children: [AddDynamicName(builder, "left"), inner]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), outer]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.PhiSites.Count.ShouldBe(2);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["left"] = new Avm1Number(left);
        machine.Globals["middle"] = new Avm1Number(middle);
        machine.Globals["right"] = new Avm1Number(right);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["result"].AsNumber.ShouldBe(expected);
    }

    [Fact]
    public void Nested_join_with_outer_value_keeps_all_live_transports_distinct()
    {
        var builder = new Avm1SourceArena.Builder();
        var inner = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalOr,
            children:
            [
                AddDynamicName(builder, "middle"),
                AddDynamicName(builder, "right")
            ]);
        var outer = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children: [AddDynamicName(builder, "left"), inner]);
        var sum = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children: [AddIntegerLiteral(builder, 100), outer]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), sum]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var options = CreateFunction2Options(registerCount: 3, temporaryCount: 3);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.Count.ShouldBe(3);
        foreach (var testCase in new[]
        {
            (Left: 0, Middle: 0, Right: 7, Result: 100),
            (Left: 1, Middle: 4, Right: 7, Result: 104),
            (Left: 1, Middle: 0, Right: 7, Result: 107)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["left"] = new Avm1Number(testCase.Left);
            machine.Globals["middle"] = new Avm1Number(testCase.Middle);
            machine.Globals["right"] = new Avm1Number(testCase.Right);
            machine.Execute(
                artifact.ActionBody.ShouldNotBeNull().Actions,
                strict: true);
            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
        }
    }

    [Fact]
    public void Compiled_short_circuit_semantically_round_trips_through_source_hir()
    {
        var builder = new Avm1SourceArena.Builder();
        var logical = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children:
            [
                AddDynamicName(builder, "left"),
                AddDynamicName(builder, "right")
            ]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), logical]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var method = CreateMethod(builder, [statement]);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);
        var firstArtifact = Compile(method, options);
        firstArtifact.Succeeded.ShouldBeTrue(Describe(firstArtifact));
        var projected = Avm1Decompiler.DecompileMethod(
            firstArtifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();

        var secondArtifact = Compile(projected, options);

        secondArtifact.Succeeded.ShouldBeTrue(Describe(secondArtifact));
        projected.GetAs2Text().ShouldContain("result =");
        foreach (var testCase in new[]
        {
            (Left: 0, Right: 8, Result: 0),
            (Left: 3, Right: 8, Result: 8)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["left"] = new Avm1Number(testCase.Left);
            machine.Globals["right"] = new Avm1Number(testCase.Right);
            machine.Execute(
                secondArtifact.ActionBody.ShouldNotBeNull().Actions,
                strict: true);
            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
        }
    }

    [Fact]
    public void Adobe_three_operand_logical_if_in_nested_function_semantically_round_trips()
    {
        var builder = new Avm1SourceArena.Builder();
        var parameter = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "position",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            Avm1SourceSymbolFlags.DeclarationProvided);

        SourceExpressionIndex AddComparison(string value) => AddBinaryExpression(
            builder,
            Avm1SourceOperator.Equal,
            AddSymbolReference(builder, parameter),
            AddStringLiteral(builder, value));

        var firstPair = AddBinaryExpression(
            builder,
            Avm1SourceOperator.LogicalOr,
            AddComparison("T"),
            AddComparison("TL"));
        var condition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.LogicalOr,
            firstPair,
            AddComparison("TR"));
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                Avm1SourceOperator.Assign,
                children:
                [
                    AddDynamicName(builder, "result"),
                    AddIntegerLiteral(builder, 1)
                ]));
        var conditional = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: condition,
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: [body])
            ]);
        var function = builder.AddFunction(
            SourceSymbolIndex.Invalid,
            builder.AddStatement(
                Avm1SourceStatementKind.Block,
                children: [conditional]),
            Avm1SourceFunctionFlags.None,
            SourceOriginIndex.Invalid,
            parameters: [parameter]);
        var functionLiteral = builder.AddExpression(
            Avm1SourceExpressionKind.FunctionLiteral,
            function: function);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "handler"), functionLiteral]);
        var method = CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: assignment)
            ]);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1) with
        {
            ExpressionEvaluationMode =
                Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
        };
        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}")) +
            Environment.NewLine + projected.GetAs2Text());
    }

    [Fact]
    public void Rejects_malformed_conditional_before_stack_lowering()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 5, 3, 18);
        var malformed = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(builder, "condition"),
                AddIntegerLiteral(builder, 1)
            ],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: malformed);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains(
                "condition, true, and false",
                StringComparison.Ordinal));
    }

    [Fact]
    public void While_loop_executes_with_explicit_condition_and_back_edge()
    {
        var artifact = Compile(CreateCountingWhileMethod());

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Blocks.Count.ShouldBe(4);
        var target = artifact.Mir.ControlTargets.ShouldHaveSingleItem();
        target.Kind.ShouldBe(Avm1MirControlTargetKind.Loop);
        target.ContinueBlock.ShouldBe(new Avm1MirBlockIndex(1));
        target.BreakBlock.ShouldBe(new Avm1MirBlockIndex(3));
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["i"] = new Avm1Number(0);
        machine.Globals["sum"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["i"].AsNumber.ShouldBe(4d);
        machine.Globals["sum"].AsNumber.ShouldBe(6d);
    }

    [Fact]
    public void Do_while_executes_the_body_before_a_false_condition()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Add,
            AddDynamicName(builder, "count"),
            AddIntegerLiteral(builder, 1));
        var body = AddAssignmentStatement(builder, "count", increment);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.DoWhile,
            expression: AddDynamicName(builder, "condition"),
            children: [body]);
        var artifact = Compile(CreateMethod(builder, [loop]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["count"] = new Avm1Number(0);
        machine.Globals["condition"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void Adobe_short_circuit_do_while_semantically_round_trips()
    {
        var builder = new Avm1SourceArena.Builder();
        var condition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.LogicalAnd,
            AddDynamicName(builder, "left"),
            AddDynamicName(builder, "right"));
        var increment = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var reset = AddAssignmentStatement(
            builder,
            "count",
            AddIntegerLiteral(builder, 0));
        var body = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "increment"),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: [increment]),
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children: [reset])
            ]);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.DoWhile,
            expression: condition,
            children: [body]);
        var method = CreateMethod(builder, [loop]);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1) with
        {
            ExpressionEvaluationMode =
                Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
        };
        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}")) +
            Environment.NewLine + projected.GetAs2Text());
    }

    [Fact]
    public void Short_circuit_recovery_keeps_a_local_used_after_the_condition()
    {
        var builder = new Avm1SourceArena.Builder();
        var offset = AddLocalSymbol(builder, "offset");
        var open = AddLocalSymbol(builder, "open");
        var close = AddLocalSymbol(builder, "close");

        SourceExpressionIndex AddIndexOf(string value)
        {
            var member = builder.AddExpression(
                Avm1SourceExpressionKind.MemberAccess,
                children:
                [
                    AddDynamicName(builder, "input"),
                    AddStringLiteral(builder, "indexOf")
                ]);
            return builder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children:
                [
                    member,
                    AddStringLiteral(builder, value),
                    AddSymbolReference(builder, offset)
                ]);
        }

        var validRange = AddBinaryExpression(
            builder,
            Avm1SourceOperator.LogicalAnd,
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.LogicalAnd,
                AddBinaryExpression(
                    builder,
                    Avm1SourceOperator.NotEqual,
                    AddSymbolReference(builder, open),
                    AddIntegerLiteral(builder, -1)),
                AddBinaryExpression(
                    builder,
                    Avm1SourceOperator.NotEqual,
                    AddSymbolReference(builder, close),
                    AddIntegerLiteral(builder, -1))),
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddSymbolReference(builder, open),
                AddSymbolReference(builder, close)));
        var invalidRange = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.LogicalNot,
            children: [validRange]);
        var guard = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: invalidRange,
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children:
                    [
                        builder.AddStatement(Avm1SourceStatementKind.Break)
                    ])
            ]);
        var consume = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "consume"),
                AddSymbolReference(builder, open),
                AddSymbolReference(builder, close)
            ]);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.DoWhile,
            expression: AddBooleanLiteral(builder, true),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddIndexOf("{"),
                    symbol: open),
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddIndexOf("}"),
                    symbol: close),
                guard,
                builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: consume)
            ]);
        var method = CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddIntegerLiteral(builder, 0),
                    symbol: offset),
                loop
            ]);
        var options = new Avm1CompilationOptions(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = byte.MaxValue,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(1, 254),
            SourceRegisterAllocationMode =
                Avm1SourceRegisterAllocationMode.PreserveSourceSymbols,
            ExpressionEvaluationMode =
                Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
        };

        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7,
            new FunctionContext((FunctionFlags)0, [])).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(equivalence) + Environment.NewLine +
                projected.GetAs2Text());
    }

    [Fact]
    public void Register_self_update_in_a_loop_condition_is_not_hidden_as_transport()
    {
        var builder = new Avm1SourceArena.Builder();
        var counter = AddLocalSymbol(builder, "counter");
        var limit = AddLocalSymbol(builder, "limit");
        var result = AddLocalSymbol(builder, "result");

        SourceStatementIndex Declare(SourceSymbolIndex symbol, int value) =>
            builder.AddStatement(
                Avm1SourceStatementKind.VariableDeclaration,
                expression: AddIntegerLiteral(builder, value),
                symbol: symbol);
        SourceStatementIndex AddAssign(SourceSymbolIndex symbol, int value) =>
            builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: builder.AddExpression(
                    Avm1SourceExpressionKind.Assignment,
                    Avm1SourceOperator.AddAssign,
                    children:
                    [
                        AddSymbolReference(builder, symbol),
                        AddIntegerLiteral(builder, value)
                    ]));

        var breakIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.GreaterOrEqual,
                AddSymbolReference(builder, counter),
                AddSymbolReference(builder, limit)),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Block,
                    children:
                    [
                        builder.AddStatement(
                            Avm1SourceStatementKind.Break)
                    ])
            ]);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.DoWhile,
            expression: AddBooleanLiteral(builder, true),
            children:
            [
                AddAssign(counter, 1),
                breakIf,
                AddAssign(result, 1)
            ]);
        var method = CreateMethod(
            builder,
            [
                Declare(counter, 0),
                Declare(limit, 3),
                Declare(result, 0),
                loop,
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddSymbolReference(builder, result))
            ]);
        var options = CreateFunction2Options(
            registerCount: 8,
            temporaryCount: 2) with
        {
            SourceRegisterAllocationMode =
                Avm1SourceRegisterAllocationMode.PreserveSourceSymbols
        };

        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var decompilation = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7,
            new FunctionContext((FunctionFlags)0, []));
        var projected = Avm1SourceProjector.ProjectMethod(
            decompilation,
            registerAliases: null,
            includedStatementNodes: null,
            coalesceNonInterferingRegisterVersions: false);
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(equivalence) + Environment.NewLine +
                projected.GetAs2Text());
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Execute(artifact.ActionBody.Actions, strict: true);
        machine.ReturnValue.AsNumber.ShouldBe(2d);
    }

    [Fact]
    public void Do_while_continue_executes_the_bottom_condition()
    {
        var builder = new Avm1SourceArena.Builder();
        var incrementIterations = AddAssignmentStatement(
            builder,
            "iterations",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "iterations"),
                AddIntegerLiteral(builder, 1)));
        var emergencyBreak = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Greater,
                AddDynamicName(builder, "iterations"),
                AddIntegerLiteral(builder, 5)),
            children: [builder.AddStatement(Avm1SourceStatementKind.Break)]);
        var continueStatement = builder.AddStatement(
            Avm1SourceStatementKind.Continue);
        var incrementChecks = AddAssignmentExpression(
            builder,
            "checks",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "checks"),
                AddIntegerLiteral(builder, 1)));
        var condition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Less,
            incrementChecks,
            AddIntegerLiteral(builder, 3));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.DoWhile,
            expression: condition,
            children: [incrementIterations, emergencyBreak, continueStatement]);
        var artifact = Compile(CreateMethod(builder, [loop]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var controlTarget = artifact.Mir.ControlTargets.ShouldHaveSingleItem();
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Continue)
            .Target.ShouldBe(controlTarget.ContinueBlock);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["iterations"] = new Avm1Number(0);
        machine.Globals["checks"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["iterations"].AsNumber.ShouldBe(3d);
        machine.Globals["checks"].AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void While_break_and_continue_resolve_to_the_same_loop_target()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = AddAssignmentStatement(
            builder,
            "i",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 1)));
        var continueStatement = builder.AddStatement(Avm1SourceStatementKind.Continue);
        var continueIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Equal,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 2)),
            children: [continueStatement]);
        var breakStatement = builder.AddStatement(Avm1SourceStatementKind.Break);
        var breakIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Equal,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 5)),
            children: [breakStatement]);
        var addToSum = AddAssignmentStatement(
            builder,
            "sum",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "sum"),
                AddDynamicName(builder, "i")));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 6)),
            children: [increment, continueIf, breakIf, addToSum]);
        var artifact = Compile(CreateMethod(builder, [loop]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var jumps = artifact.Mir.Instructions.Where(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Break or
                Avm1MirInstructionKind.Continue).ToArray();
        jumps.Length.ShouldBe(2);
        jumps.Select(instruction => instruction.ControlTarget).Distinct()
            .ShouldHaveSingleItem();
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["i"] = new Avm1Number(0);
        machine.Globals["sum"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["i"].AsNumber.ShouldBe(5d);
        machine.Globals["sum"].AsNumber.ShouldBe(8d);
    }

    [Fact]
    public void For_loop_preserves_multiple_initializers_updates_and_continue_order()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var i = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "i",
            SourceTypeIndex.Invalid,
            unknown);
        var c = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "c",
            SourceTypeIndex.Invalid,
            unknown);
        var iInitializer = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 0),
            symbol: i);
        var cInitializer = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 10),
            symbol: c);
        var condition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Less,
            AddSymbolReference(builder, i),
            AddIntegerLiteral(builder, 3));
        var incrementI = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddSymbolReference(builder, i)]);
        var incrementC = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddSymbolReference(builder, c)]);
        var continueIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Equal,
                AddSymbolReference(builder, i),
                AddIntegerLiteral(builder, 1)),
            children: [builder.AddStatement(Avm1SourceStatementKind.Continue)]);
        var addToSum = AddAssignmentStatement(
            builder,
            "sum",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "sum"),
                AddSymbolReference(builder, c)));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            expression: condition,
            children: [continueIf, addToSum],
            initializers: [iInitializer, cInitializer],
            expressions: [incrementI, incrementC]);
        var method = CreateMethod(builder, [loop]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        method.GetAs2Text().ShouldContain("for (var i = 0, c = 10;");
        method.GetAs2Text().ShouldContain("; i++, c++)");
        var controlTarget = artifact.Mir.ControlTargets.ShouldHaveSingleItem();
        var continueInstruction = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Continue);
        continueInstruction.Target.ShouldBe(controlTarget.ContinueBlock);
        continueInstruction.Target.ShouldNotBe(controlTarget.BreakBlock);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["sum"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["i"].AsNumber.ShouldBe(3d);
        machine.Globals["c"].AsNumber.ShouldBe(13d);
        machine.Globals["sum"].AsNumber.ShouldBe(22d);
    }

    [Fact]
    public void Infinite_for_loop_reaches_following_code_only_through_break()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var breakIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Equal,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 3)),
            children: [builder.AddStatement(Avm1SourceStatementKind.Break)]);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            children: [increment, breakIf]);
        var after = AddAssignmentStatement(builder, "done", 1);
        var artifact = Compile(CreateMethod(builder, [loop, after]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["count"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(3d);
        machine.Globals["done"].AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void Labeled_break_from_nested_loop_targets_the_outer_exit()
    {
        var builder = new Avm1SourceArena.Builder();
        var outerLabel = builder.AddLabel("outer");
        var incrementResult = AddAssignmentStatement(
            builder,
            "result",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "result"),
                AddIntegerLiteral(builder, 1)));
        var labeledBreak = builder.AddStatement(
            Avm1SourceStatementKind.Break,
            label: outerLabel);
        var innerLoop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddIntegerLiteral(builder, 1),
            children: [incrementResult, labeledBreak]);
        var wrongPath = AddAssignmentStatement(builder, "result", 99);
        var incrementOuter = AddAssignmentStatement(
            builder,
            "outerCount",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "outerCount"),
                AddIntegerLiteral(builder, 1)));
        var outerLoop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddDynamicName(builder, "outerCount"),
                AddIntegerLiteral(builder, 3)),
            label: outerLabel,
            children: [innerLoop, wrongPath, incrementOuter]);
        var artifact = Compile(CreateMethod(builder, [outerLoop]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.ControlTargets.Count.ShouldBe(2);
        var breakInstruction = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Break);
        breakInstruction.ControlTarget.ShouldBe(new Avm1MirControlTargetIndex(0));
        breakInstruction.Target.ShouldBe(artifact.Mir[breakInstruction.ControlTarget].BreakBlock);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["result"] = new Avm1Number(0);
        machine.Globals["outerCount"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["result"].AsNumber.ShouldBe(1d);
        machine.Globals["outerCount"].AsNumber.ShouldBe(0d);
    }

    [Fact]
    public void Switch_evaluates_selector_once_and_case_values_in_source_order()
    {
        var builder = new Avm1SourceArena.Builder();
        var selector = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Add,
            AddAssignmentExpression(
                builder,
                "selectorReads",
                AddBinaryExpression(
                    builder,
                    Avm1SourceOperator.Add,
                    AddDynamicName(builder, "selectorReads"),
                    AddIntegerLiteral(builder, 1))),
            AddIntegerLiteral(builder, 11));
        var firstCase = AddSwitchCase(
            builder,
            AddAssignmentExpression(builder, "caseOrder", 1),
            AddAssignmentStatement(builder, "result", 1),
            builder.AddStatement(Avm1SourceStatementKind.Break));
        var matchingCase = AddSwitchCase(
            builder,
            AddAssignmentExpression(builder, "caseOrder", 12),
            AddAssignmentStatement(builder, "result", 2),
            builder.AddStatement(Avm1SourceStatementKind.Break));
        var skippedCase = AddSwitchCase(
            builder,
            AddAssignmentExpression(builder, "caseOrder", 123),
            AddAssignmentStatement(builder, "result", 3),
            builder.AddStatement(Avm1SourceStatementKind.Break));
        var defaultLabel = AddSwitchDefault(
            builder,
            AddAssignmentStatement(builder, "result", 4));
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: selector,
            children: [firstCase, matchingCase, skippedCase, defaultLabel]);
        var artifact = Compile(
            CreateMethod(builder, [@switch]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(1);
        var target = artifact.Mir.ControlTargets.ShouldHaveSingleItem();
        target.Kind.ShouldBe(Avm1MirControlTargetKind.Switch);
        target.ContinueBlock.IsValid.ShouldBeFalse();
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Binary &&
            instruction.Operator is Avm1MirOperator.StrictEqual).ShouldBe(3);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionStrictEquals>().Count().ShouldBe(3);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["selectorReads"] = new Avm1Number(0);
        machine.Globals["caseOrder"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.Globals["selectorReads"].AsNumber.ShouldBe(1d);
        machine.Globals["caseOrder"].AsNumber.ShouldBe(12d);
        machine.Globals["result"].AsNumber.ShouldBe(2d);
    }

    [Fact]
    public void Switch_preserves_middle_default_and_lexical_fallthrough()
    {
        var builder = new Avm1SourceArena.Builder();
        var caseOne = AddSwitchCase(
            builder,
            AddIntegerLiteral(builder, 1),
            AddAssignmentStatement(
                builder,
                "result",
                AddBinaryExpression(
                    builder,
                    Avm1SourceOperator.Add,
                    AddDynamicName(builder, "result"),
                    AddIntegerLiteral(builder, 1))));
        var defaultLabel = AddSwitchDefault(
            builder,
            AddAssignmentStatement(
                builder,
                "result",
                AddBinaryExpression(
                    builder,
                    Avm1SourceOperator.Add,
                    AddDynamicName(builder, "result"),
                    AddIntegerLiteral(builder, 10))));
        var caseTwo = AddSwitchCase(
            builder,
            AddIntegerLiteral(builder, 2),
            AddAssignmentStatement(
                builder,
                "result",
                AddBinaryExpression(
                    builder,
                    Avm1SourceOperator.Add,
                    AddDynamicName(builder, "result"),
                    AddIntegerLiteral(builder, 100))),
            builder.AddStatement(Avm1SourceStatementKind.Break));
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "kind"),
            children: [caseOne, defaultLabel, caseTwo]);
        var method = CreateMethod(builder, [@switch]);
        var artifact = Compile(
            method,
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var source = method.GetAs2Text();
        source.IndexOf("case 1:", StringComparison.Ordinal).ShouldBeLessThan(
            source.IndexOf("default:", StringComparison.Ordinal));
        source.IndexOf("default:", StringComparison.Ordinal).ShouldBeLessThan(
            source.IndexOf("case 2:", StringComparison.Ordinal));
        foreach (var testCase in new[]
        {
            (Kind: 1, Result: 111),
            (Kind: 2, Result: 100),
            (Kind: 9, Result: 110)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["kind"] = new Avm1Number(testCase.Kind);
            machine.Globals["result"] = new Avm1Number(0);

            machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
        }
    }

    [Fact]
    public void Continue_inside_switch_targets_the_enclosing_loop()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = AddAssignmentStatement(
            builder,
            "i",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 1)));
        var caseOne = AddSwitchCase(
            builder,
            AddIntegerLiteral(builder, 1),
            builder.AddStatement(Avm1SourceStatementKind.Continue));
        var defaultLabel = AddSwitchDefault(
            builder,
            builder.AddStatement(Avm1SourceStatementKind.Break));
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "i"),
            children: [caseOne, defaultLabel]);
        var addToSum = AddAssignmentStatement(
            builder,
            "sum",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "sum"),
                AddDynamicName(builder, "i")));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 3)),
            children: [increment, @switch, addToSum]);
        var artifact = Compile(
            CreateMethod(builder, [loop]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.ControlTargets.Select(target => target.Kind).ShouldBe([
            Avm1MirControlTargetKind.Loop,
            Avm1MirControlTargetKind.Switch
        ]);
        var continueInstruction = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Continue);
        continueInstruction.ControlTarget.ShouldBe(new Avm1MirControlTargetIndex(0));
        var switchBreak = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Break);
        switchBreak.ControlTarget.ShouldBe(new Avm1MirControlTargetIndex(1));
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["i"] = new Avm1Number(0);
        machine.Globals["sum"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["i"].AsNumber.ShouldBe(3d);
        machine.Globals["sum"].AsNumber.ShouldBe(5d);
    }

    [Fact]
    public void Labeled_break_inside_switch_can_exit_an_outer_loop()
    {
        var builder = new Avm1SourceArena.Builder();
        var outerLabel = builder.AddLabel("outer");
        var labeledBreak = builder.AddStatement(
            Avm1SourceStatementKind.Break,
            label: outerLabel);
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddIntegerLiteral(builder, 1),
            children:
            [
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 1),
                    labeledBreak),
                AddSwitchDefault(
                    builder,
                    builder.AddStatement(Avm1SourceStatementKind.Break))
            ]);
        var wrongPath = AddAssignmentStatement(builder, "wrongPath", 1);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddIntegerLiteral(builder, 1),
            label: outerLabel,
            children: [@switch, wrongPath]);
        var artifact = Compile(
            CreateMethod(builder, [loop]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Where(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Break)
            .Select(instruction => instruction.ControlTarget)
            .ShouldBe([
                new Avm1MirControlTargetIndex(0),
                new Avm1MirControlTargetIndex(1)
            ]);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["wrongPath"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["wrongPath"].AsNumber.ShouldBe(0d);
    }

    [Fact]
    public void Rejects_labeled_continue_whose_target_is_a_switch()
    {
        var builder = new Avm1SourceArena.Builder();
        var switchLabel = builder.AddLabel("choices");
        var invalidContinue = builder.AddStatement(
            Avm1SourceStatementKind.Continue,
            label: switchLabel);
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddIntegerLiteral(builder, 1),
            label: switchLabel,
            children:
            [
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 1),
                    invalidContinue)
            ]);

        var artifact = Compile(
            CreateMethod(builder, [@switch]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Message.Contains(
                "does not refer to an active loop",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_duplicate_switch_defaults_before_stack_lowering()
    {
        var builder = new Avm1SourceArena.Builder();
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddIntegerLiteral(builder, 1),
            children:
            [
                AddSwitchDefault(builder),
                AddSwitchDefault(builder)
            ]);

        var artifact = Compile(CreateMethod(builder, [@switch]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Message.Contains("more than one default", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_non_label_switch_children_before_stack_lowering()
    {
        var builder = new Avm1SourceArena.Builder();
        var invalidChild = AddAssignmentStatement(builder, "value", 1);
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddIntegerLiteral(builder, 1),
            children: [invalidChild]);

        var artifact = Compile(CreateMethod(builder, [@switch]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Message.Contains("not a case or default", StringComparison.Ordinal));
    }

    [Fact]
    public void Switch_spills_persistent_selector_without_private_registers()
    {
        var builder = new Avm1SourceArena.Builder();
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddIntegerLiteral(builder, 1),
            children:
            [
                AddSwitchCase(builder, AddIntegerLiteral(builder, 1))
            ]);
        var artifact = Compile(
            CreateMethod(builder, [@switch]),
            CreateFunction2Options(registerCount: 0, temporaryCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(1);
        AssertActivationSpills(artifact, expectedCount: 1);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action is ActionDefineLocal);
    }

    [Fact]
    public void Default_only_switch_evaluates_the_selector_without_a_temporary()
    {
        var builder = new Avm1SourceArena.Builder();
        var selector = AddAssignmentExpression(
            builder,
            "selectorReads",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "selectorReads"),
                AddIntegerLiteral(builder, 1)));
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: selector,
            children:
            [
                AddSwitchDefault(
                    builder,
                    AddAssignmentStatement(builder, "result", 7))
            ]);
        var artifact = Compile(CreateMethod(builder, [@switch]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(0);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["selectorReads"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["selectorReads"].AsNumber.ShouldBe(1d);
        machine.Globals["result"].AsNumber.ShouldBe(7d);
    }

    [Fact]
    public void Switch_semantically_round_trips_through_source_hir()
    {
        var builder = new Avm1SourceArena.Builder();
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "kind"),
            children:
            [
                AddSwitchCase(
                    builder,
                    AddStringLiteral(builder, "a"),
                    AddAssignmentStatement(builder, "result", 1),
                    builder.AddStatement(Avm1SourceStatementKind.Break)),
                AddSwitchCase(
                    builder,
                    AddStringLiteral(builder, "b"),
                    AddAssignmentStatement(builder, "result", 2)),
                AddSwitchCase(
                    builder,
                    AddStringLiteral(builder, "c"),
                    AddAssignmentStatement(builder, "result", 3),
                    builder.AddStatement(Avm1SourceStatementKind.Break)),
                AddSwitchDefault(
                    builder,
                    AddAssignmentStatement(builder, "result", 4))
            ]);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);
        var firstArtifact = Compile(CreateMethod(builder, [@switch]), options);
        firstArtifact.Succeeded.ShouldBeTrue(Describe(firstArtifact));
        var projected = Avm1Decompiler.DecompileMethod(
            firstArtifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();

        var secondArtifact = Compile(projected, options);

        secondArtifact.Succeeded.ShouldBeTrue(Describe(secondArtifact));
        projected.GetAs2Text().ShouldContain("switch (kind)");
        foreach (var testCase in new[]
        {
            (Kind: "a", Result: 1),
            (Kind: "b", Result: 3),
            (Kind: "c", Result: 3),
            (Kind: "z", Result: 4)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["kind"] = new Avm1String(testCase.Kind);
            machine.Execute(
                secondArtifact.ActionBody.ShouldNotBeNull().Actions,
                strict: true);
            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
        }
    }

    [Fact]
    public void Terminal_switch_keeps_a_conditional_return_inside_its_case()
    {
        var builder = new Avm1SourceArena.Builder();
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(builder, "shift"),
                AddIntegerLiteral(builder, 20),
                AddIntegerLiteral(builder, 21)
            ]);
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "kind"),
            children:
            [
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 1),
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: AddIntegerLiteral(builder, 10))),
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 2),
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: conditional)),
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 3),
                    builder.AddStatement(
                        Avm1SourceStatementKind.Return,
                        expression: AddIntegerLiteral(builder, 30)))
            ]);
        var nullLiteral = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(Avm1SourceLiteralKind.Null));
        var method = CreateMethod(
            builder,
            [
                @switch,
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: nullLiteral)
            ]);
        var artifact = Compile(
            method,
            CreateFunction2Options(registerCount: 2, temporaryCount: 2));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}")) +
            Environment.NewLine + projected.GetAs2Text());
    }

    [Fact]
    public void Switch_uses_strict_equality_without_numeric_coercion()
    {
        var builder = new Avm1SourceArena.Builder();
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddStringLiteral(builder, "1"),
            children:
            [
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 1),
                    AddAssignmentStatement(builder, "result", 1),
                    builder.AddStatement(Avm1SourceStatementKind.Break)),
                AddSwitchDefault(
                    builder,
                    AddAssignmentStatement(builder, "result", 2))
            ]);
        var artifact = Compile(
            CreateMethod(builder, [@switch]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["result"].AsNumber.ShouldBe(2d);
    }

    [Fact]
    public void Nested_switch_bodies_reuse_one_virtual_temporary_slot()
    {
        var builder = new Avm1SourceArena.Builder();
        var innerSwitch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "inner"),
            children:
            [
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 2),
                    AddAssignmentStatement(builder, "result", 7),
                    builder.AddStatement(Avm1SourceStatementKind.Break)),
                AddSwitchDefault(
                    builder,
                    AddAssignmentStatement(builder, "result", 8))
            ]);
        var outerSwitch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "outer"),
            children:
            [
                AddSwitchCase(
                    builder,
                    AddIntegerLiteral(builder, 1),
                    innerSwitch,
                    builder.AddStatement(Avm1SourceStatementKind.Break)),
                AddSwitchDefault(
                    builder,
                    AddAssignmentStatement(builder, "result", 9))
            ]);
        var artifact = Compile(
            CreateMethod(builder, [outerSwitch]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(1);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
        artifact.Mir.ControlTargets.Select(target => target.Kind).ShouldBe([
            Avm1MirControlTargetKind.Switch,
            Avm1MirControlTargetKind.Switch
        ]);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["outer"] = new Avm1Number(1);
        machine.Globals["inner"] = new Avm1Number(2);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["result"].AsNumber.ShouldBe(7d);
    }

    [Fact]
    public void Unsupported_switch_case_expression_returns_a_diagnostic_artifact()
    {
        var builder = new Avm1SourceArena.Builder();
        var unsupportedCaseValue = builder.AddExpression(
            Avm1SourceExpressionKind.Opaque);
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddIntegerLiteral(builder, 1),
            children:
            [
                AddSwitchCase(builder, unsupportedCaseValue),
                AddSwitchDefault(builder)
            ]);

        var artifact = Compile(
            CreateMethod(builder, [@switch]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP101" &&
            diagnostic.Message.Contains("Opaque", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Avm1SourceStatementKind.Break)]
    [InlineData(Avm1SourceStatementKind.Continue)]
    public void Rejects_control_jump_outside_an_active_scope(
        Avm1SourceStatementKind kind)
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 9, 12, 17);
        var jump = builder.AddStatement(kind, origin: origin);

        var artifact = Compile(CreateMethod(builder, [jump]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains("outside a control scope", StringComparison.Ordinal));
    }

    [Fact]
    public void Counting_while_semantically_round_trips_through_source_hir()
    {
        var firstArtifact = Compile(CreateCountingWhileMethod());
        firstArtifact.Succeeded.ShouldBeTrue(Describe(firstArtifact));
        var projected = Avm1Decompiler.DecompileMethod(
            firstArtifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();

        var secondArtifact = Compile(projected);

        secondArtifact.Succeeded.ShouldBeTrue(Describe(secondArtifact));
        projected.GetAs2Text().ShouldContain("while (");
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["i"] = new Avm1Number(0);
        machine.Globals["sum"] = new Avm1Number(0);
        machine.Execute(
            secondArtifact.ActionBody.ShouldNotBeNull().Actions,
            strict: true);
        machine.Globals["i"].AsNumber.ShouldBe(4d);
        machine.Globals["sum"].AsNumber.ShouldBe(6d);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adobe_local_postfix_while_condition_semantically_round_trips(
        bool compilerGenerated)
    {
        var builder = new Avm1SourceArena.Builder();
        var remaining = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "remaining",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            compilerGenerated
                ? Avm1SourceSymbolFlags.CompilerGenerated
                : Avm1SourceSymbolFlags.None);
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixDecrement,
            children: [AddSymbolReference(builder, remaining)]);
        var method = CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddIntegerLiteral(builder, 2),
                    symbol: remaining),
                builder.AddStatement(
                    Avm1SourceStatementKind.While,
                    expression: condition,
                    children:
                    [
                        AddAssignmentStatement(
                            builder,
                            "last",
                            AddSymbolReference(builder, remaining))
                    ])
            ]);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2) with
        {
            ExpressionEvaluationMode =
                Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
        };

        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var decompilation = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7,
            new FunctionContext((FunctionFlags)0, []));
        var projected = decompilation.ProjectSource();
        var source = projected.GetAs2Text();
        var registerSsa = string.Join(
            Environment.NewLine,
            decompilation.RegisterSsa.PhiNodes.Select(phi =>
                $"phi b{phi.Block.Value} r{phi.Register}v{phi.Version} <- " +
                string.Join(", ", phi.Predecessors.Zip(
                    phi.IncomingVersions,
                    (block, version) => $"b{block.Value}:v{version}")))
            .Concat(decompilation.RegisterSsa.Accesses.Select(access =>
                $"{access.Kind} ir{access.Instruction.Value} " +
                $"r{access.Register}v{access.Version} source={access.Source}")));
        (source.Contains("while (", StringComparison.Ordinal) &&
            source.Contains("--)", StringComparison.Ordinal)).ShouldBeTrue(
            source + Environment.NewLine + registerSsa + Environment.NewLine +
            ShockwaveFlash.Avm1.Text.Avm1Disassembler.Disassemble(
                artifact.ActionBody.Actions,
                swfVersion: 7));
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);
        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(equivalence));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public void Adobe_call_condition_while_round_trips_as_a_loop(
        bool logicalAnd,
        int operandCount)
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var text = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "text",
            SourceTypeIndex.Invalid,
            unknown,
            Avm1SourceSymbolFlags.DeclarationProvided);
        var index = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "index",
            SourceTypeIndex.Invalid,
            unknown);

        SourceExpressionIndex AddCharacterComparison(string value)
        {
            var member = builder.AddExpression(
                Avm1SourceExpressionKind.MemberAccess,
                children:
                [
                    AddSymbolReference(builder, text),
                    AddStringLiteral(builder, "charAt")
                ]);
            var call = builder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children:
                [
                    member,
                    AddSymbolReference(builder, index)
                ]);
            return AddBinaryExpression(
                builder,
                Avm1SourceOperator.Equal,
                call,
                AddStringLiteral(builder, value));
        }

        string[] characters = [" ", "\t", "\r", "\n"];
        var condition = AddCharacterComparison(characters[0]);
        for (var operand = 1; operand < operandCount; operand++)
        {
            condition = AddBinaryExpression(
                builder,
                logicalAnd
                    ? Avm1SourceOperator.LogicalAnd
                    : Avm1SourceOperator.LogicalOr,
                condition,
                AddCharacterComparison(characters[operand]));
        }
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children:
            [
                AddSymbolReference(builder, index),
                AddIntegerLiteral(builder, 1)
            ]);
        var method = CreateMethod(
            builder,
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.VariableDeclaration,
                    expression: AddIntegerLiteral(builder, 0),
                    symbol: index),
                builder.AddStatement(
                    Avm1SourceStatementKind.While,
                    expression: condition,
                    children:
                    [
                        builder.AddStatement(
                            Avm1SourceStatementKind.Expression,
                            expression: increment)
                    ])
            ],
            [text]);
        var artifact = Compile(
            method,
            new Avm1CompilationOptions(7)
            {
                RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
                RegisterCount = byte.MaxValue,
                ReservedTemporaryRegisters =
                    new Avm1TemporaryRegisterRange(1, 254),
                ExpressionEvaluationMode =
                    Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var decompilation = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7,
            new FunctionContext(
                (FunctionFlags)0,
                [new FunctionParameter(0, "text")]));
        var projected = decompilation.ProjectSource();
        var source = projected.GetAs2Text();
        var pcode = ShockwaveFlash.Avm1.Text.Avm1Disassembler.Disassemble(
            artifact.ActionBody.Actions,
            swfVersion: 7);
        var loops = string.Join(
            Environment.NewLine,
            decompilation.LoopAnalysis.Loops.Select(loop =>
                $"loop b{loop.Header.Value}<-b{loop.Tail.Value} " +
                $"blocks=[{string.Join(',', loop.Blocks.Select(block => block.Value))}] " +
                $"exits=[{string.Join(',', loop.Exits.Select(block => block.Value))}]"));

        source.Contains("while (", StringComparison.Ordinal).ShouldBeTrue(
            source + Environment.NewLine + Environment.NewLine + loops +
            Environment.NewLine + Environment.NewLine + pcode);
        source.Contains("charAt", StringComparison.Ordinal).ShouldBeTrue(
            source + Environment.NewLine + Environment.NewLine + loops +
            Environment.NewLine + Environment.NewLine + pcode);
        source.Split("charAt", StringSplitOptions.None).Length.ShouldBe(
            operandCount + 1,
            source + Environment.NewLine + Environment.NewLine + loops +
            Environment.NewLine + Environment.NewLine + pcode);
        source.Contains(logicalAnd ? " && " : " || ", StringComparison.Ordinal)
            .ShouldBeTrue(
                source + Environment.NewLine + Environment.NewLine + loops +
                Environment.NewLine + Environment.NewLine + pcode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adobe_logical_for_condition_semantically_round_trips(
        bool preloadThis)
    {
        var builder = new Avm1SourceArena.Builder();
        var thisSymbol = builder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Object),
            Avm1SourceSymbolFlags.DeclarationProvided);
        var index = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "_loc5_v1",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            Avm1SourceSymbolFlags.CompilerGenerated);
        var getSize = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                builder.AddExpression(
                    Avm1SourceExpressionKind.MemberAccess,
                    children:
                    [
                        AddSymbolReference(builder, thisSymbol),
                        AddStringLiteral(builder, "getSize")
                    ])
            ]);
        var left = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Less,
            AddSymbolReference(builder, index),
            getSize);
        var right = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Less,
            AddSymbolReference(builder, index),
            builder.AddExpression(
                Avm1SourceExpressionKind.MemberAccess,
                children:
                [
                    AddSymbolReference(builder, thisSymbol),
                    AddStringLiteral(builder, "limit")
                ]));
        var condition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.LogicalAnd,
            left,
            right);
        var initializer = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 0),
            symbol: index);
        var update = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children:
            [
                AddSymbolReference(builder, index),
                AddIntegerLiteral(builder, 1)
            ]);
        var consume = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                builder.AddExpression(
                    Avm1SourceExpressionKind.MemberAccess,
                    children:
                    [
                        AddSymbolReference(builder, thisSymbol),
                        AddStringLiteral(builder, "consume")
                    ]),
                AddSymbolReference(builder, index)
            ]);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            expression: condition,
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: consume)
            ],
            initializers: [initializer],
            expressions: [update]);
        var secondIndex = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "_loc5_v3",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            Avm1SourceSymbolFlags.CompilerGenerated);
        var secondGetSize = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                builder.AddExpression(
                    Avm1SourceExpressionKind.MemberAccess,
                    children:
                    [
                        AddSymbolReference(builder, thisSymbol),
                        AddStringLiteral(builder, "getSize")
                    ])
            ]);
        var secondCondition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.LogicalAnd,
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddSymbolReference(builder, secondIndex),
                secondGetSize),
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddSymbolReference(builder, secondIndex),
                builder.AddExpression(
                    Avm1SourceExpressionKind.MemberAccess,
                    children:
                    [
                        AddSymbolReference(builder, thisSymbol),
                        AddStringLiteral(builder, "secondLimit")
                    ])));
        var secondInitializer = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 0),
            symbol: secondIndex);
        var secondUpdate = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children:
            [
                AddSymbolReference(builder, secondIndex),
                AddIntegerLiteral(builder, 1)
            ]);
        var secondLoop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            expression: secondCondition,
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: builder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children:
                        [
                            AddDynamicName(builder, "consumeSecond"),
                            AddSymbolReference(builder, secondIndex)
                        ]))
            ],
            initializers: [secondInitializer],
            expressions: [secondUpdate]);
        var thirdIndex = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "_loc5_v5",
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown),
            Avm1SourceSymbolFlags.CompilerGenerated);
        var thirdCondition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Less,
            AddSymbolReference(builder, thirdIndex),
            builder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children:
                [
                    builder.AddExpression(
                        Avm1SourceExpressionKind.MemberAccess,
                        children:
                        [
                            AddSymbolReference(builder, thisSymbol),
                            AddStringLiteral(builder, "getSize")
                        ])
                ]));
        var thirdInitializer = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 0),
            symbol: thirdIndex);
        var thirdUpdate = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children:
            [
                AddSymbolReference(builder, thirdIndex),
                AddIntegerLiteral(builder, 1)
            ]);
        var thirdLoop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            expression: thirdCondition,
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: builder.AddExpression(
                        Avm1SourceExpressionKind.Call,
                        children:
                        [
                            AddDynamicName(builder, "consumeThird"),
                            AddSymbolReference(builder, thirdIndex)
                        ]))
            ],
            initializers: [thirdInitializer],
            expressions: [thirdUpdate]);
        var method = CreateMethod(builder, [loop, secondLoop, thirdLoop]);
        var flags = preloadThis
            ? FunctionFlags.PreloadThis |
                FunctionFlags.SuppressArguments |
                FunctionFlags.SuppressSuper
            : (FunctionFlags)0;
        var firstTemporaryRegister = preloadThis ? (byte)2 : (byte)1;
        var artifact = new Avm1Compiler().CompileMethod(
            method,
            new Avm1CompilationOptions(7)
            {
                RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
                RegisterCount = byte.MaxValue,
                ReservedTemporaryRegisters =
                    new Avm1TemporaryRegisterRange(
                        firstTemporaryRegister,
                        checked((byte)(byte.MaxValue - firstTemporaryRegister))),
                ExpressionEvaluationMode =
                    Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
            },
            Avm1FunctionPreloadPlan.FromFlags(flags),
            CancellationToken.None);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7,
            new FunctionContext(flags, [])).ProjectSource();
        var source = projected.GetAs2Text();

        source.ShouldContain("for (");
        source.ShouldContain(" && ");
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);
        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            string.Join(
                Environment.NewLine,
                equivalence.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}")));
    }

    [Fact]
    public void Lowers_all_named_compound_assignments_through_read_compute_store()
    {
        (Avm1SourceOperator Source, Avm1MirOperator Mir)[] cases =
        [
            (Avm1SourceOperator.AddAssign, Avm1MirOperator.Add),
            (Avm1SourceOperator.SubtractAssign, Avm1MirOperator.Subtract),
            (Avm1SourceOperator.MultiplyAssign, Avm1MirOperator.Multiply),
            (Avm1SourceOperator.DivideAssign, Avm1MirOperator.Divide),
            (Avm1SourceOperator.ModuloAssign, Avm1MirOperator.Modulo),
            (Avm1SourceOperator.BitAndAssign, Avm1MirOperator.BitAnd),
            (Avm1SourceOperator.BitOrAssign, Avm1MirOperator.BitOr),
            (Avm1SourceOperator.BitXorAssign, Avm1MirOperator.BitXor),
            (Avm1SourceOperator.ShiftLeftAssign, Avm1MirOperator.ShiftLeft),
            (Avm1SourceOperator.ShiftRightAssign, Avm1MirOperator.ShiftRight),
            (Avm1SourceOperator.ShiftRightUnsignedAssign,
                Avm1MirOperator.ShiftRightUnsigned)
        ];

        foreach (var (@operator, mirOperator) in cases)
        {
            var builder = new Avm1SourceArena.Builder();
            var assignment = builder.AddExpression(
                Avm1SourceExpressionKind.Assignment,
                @operator,
                children:
                [
                    AddDynamicName(builder, "value"),
                    AddIntegerLiteral(builder, 2)
                ]);
            var statement = builder.AddStatement(
                Avm1SourceStatementKind.Expression,
                expression: assignment);

            var artifact = Compile(CreateMethod(builder, [statement]));

            artifact.Succeeded.ShouldBeTrue(
                $"{@operator}:{Environment.NewLine}{Describe(artifact)}");
            artifact.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
                Avm1MirInstructionKind.LoadLValue,
                Avm1MirInstructionKind.Constant,
                Avm1MirInstructionKind.Binary,
                Avm1MirInstructionKind.StoreLValue
            ]);
            artifact.Mir.Instructions[2].Operator.ShouldBe(mirOperator);
        }
    }

    [Fact]
    public void Prefix_increment_stores_and_produces_the_updated_value()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PrefixIncrement,
            children: [AddDynamicName(builder, "value")]);
        var assignment = AddAssignmentExpression(builder, "result", increment);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Unary)
            .Operator.ShouldBe(Avm1MirOperator.Increment);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1Number(3);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["value"].AsNumber.ShouldBe(4d);
        machine.Globals["result"].AsNumber.ShouldBe(4d);
    }

    [Fact]
    public void Value_producing_postfix_returns_the_numeric_old_value()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 13, 62, 70);
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(builder, "value")],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: increment);

        var artifact = Compile(
            CreateMethod(builder, [statement]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(1);
        artifact.Mir.Instructions
            .Where(instruction => instruction.Kind is Avm1MirInstructionKind.Unary)
            .Select(instruction => instruction.Operator)
            .ShouldBe([Avm1MirOperator.ToNumber, Avm1MirOperator.Increment]);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);

        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1String("3");

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["value"].AsNumber.ShouldBe(4d);
        machine.ReturnValue.AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void Value_producing_postfix_spills_with_an_empty_register_file()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 13, 62, 70);
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(builder, "value")],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: increment);

        var artifact = Compile(
            CreateMethod(builder, [statement]),
            CreateFunction2Options(registerCount: 0, temporaryCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        AssertActivationSpills(artifact, expectedCount: 1);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["value"] = new Avm1String("3");

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["value"].AsNumber.ShouldBe(4d);
        machine.ReturnValue.AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void Activation_spill_prefix_does_not_collide_with_source_names()
    {
        var builder = new Avm1SourceArena.Builder();
        var sourceNameRead = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: AddDynamicName(builder, "__avm1_spill_0"));
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(builder, "value")]);
        var result = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: increment);

        var artifact = Compile(
            CreateMethod(builder, [sourceNameRead, result]),
            CreateFunction2Options(registerCount: 0, temporaryCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        AssertActivationSpills(artifact, expectedCount: 1)
            .ShouldBe(["__avm1_spill_1_0"]);
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        projected.Arena.Symbols.ShouldContain(symbol =>
            projected.Arena[symbol.Name] == "__avm1_spill_1_0" &&
            symbol.Flags.HasFlag(Avm1SourceSymbolFlags.CompilerGenerated));
    }

    [Fact]
    public void Rejects_loop_without_required_condition()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 10, 20, 31);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            origin: origin);

        var artifact = Compile(CreateMethod(builder, [loop]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains("no condition", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_non_clause_statement_in_for_initializer()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 11, 32, 47);
        var invalidInitializer = builder.AddStatement(
            Avm1SourceStatementKind.Return);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            origin: origin,
            initializers: [invalidInitializer]);

        var artifact = Compile(CreateMethod(builder, [loop]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains("initializer", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_labeled_jump_to_a_different_active_scope()
    {
        var builder = new Avm1SourceArena.Builder();
        var activeLabel = builder.AddLabel("active");
        var missingLabel = builder.AddLabel("missing");
        var origin = builder.AddOrigin(0, 12, 48, 61);
        var jump = builder.AddStatement(
            Avm1SourceStatementKind.Break,
            label: missingLabel,
            origin: origin);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddIntegerLiteral(builder, 1),
            label: activeLabel,
            children: [jump]);

        var artifact = Compile(CreateMethod(builder, [loop]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains("does not resolve", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_member_lowers_to_effectful_delete_and_preserves_result()
    {
        var method = CreateMemberDeleteReturn();

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.Constant,
            Avm1MirInstructionKind.DeleteLValue,
            Avm1MirInstructionKind.Return
        ]);
        var delete = artifact.Mir.Instructions[2];
        delete.Result.IsValid.ShouldBeTrue();
        delete.Effects.ShouldBe(
            Avm1MirEffect.WritesHeap |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow);
        var lValue = artifact.Mir[delete.LValue];
        lValue.Kind.ShouldBe(Avm1MirLValueKind.Member);
        lValue.Receiver.ShouldBe(artifact.Mir.Instructions[0].Result);
        lValue.Key.ShouldBe(artifact.Mir.Instructions[1].Result);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.Delete,
                ActionOpcode.Return
            ]);
    }

    [Fact]
    public void Delete_symbol_stays_name_addressable_in_a_function2_register_file()
    {
        var builder = new Avm1SourceArena.Builder();
        var local = AddLocalSymbol(builder, "temporary");
        var declaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 1),
            symbol: local);
        var delete = builder.AddExpression(
            Avm1SourceExpressionKind.Delete,
            children: [AddSymbolReference(builder, local)]);
        var result = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: delete);

        var artifact = Compile(
            CreateMethod(builder, [declaration, result]),
            CreateFunction2Options(registerCount: 2, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var deleteInstruction = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.DeleteLValue);
        var lValue = artifact.Mir[deleteInstruction.LValue];
        lValue.Kind.ShouldBe(Avm1MirLValueKind.Name);
        lValue.Symbol.ShouldBe(local);
        lValue.NameKind.ShouldBe(Avm1MirNameKind.BoundSymbol);
        artifact.Mir.LValues.Any(candidate =>
            candidate.Symbol == local &&
            candidate.Kind == Avm1MirLValueKind.Register).ShouldBeFalse();
        artifact.ActionBody.ShouldNotBeNull().Actions
            .ShouldContain(action => action is ActionDelete2);
    }

    [Fact]
    public void Delete_computed_dynamic_name_evaluates_name_once_before_delete2()
    {
        var method = CreateComputedNameDeleteReturn();

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var delete = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.DeleteLValue);
        delete.Effects.ShouldBe(
            Avm1MirEffect.WritesDynamicScope |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow);
        var lValue = artifact.Mir[delete.LValue];
        lValue.Kind.ShouldBe(Avm1MirLValueKind.ComputedName);
        lValue.Key.IsValid.ShouldBeTrue();
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action is ActionCallFunction).ShouldBe(1);
        artifact.ActionBody.Actions.Count(action =>
            action is ActionDelete2).ShouldBe(1);
        var opcodes = artifact.ActionBody.Actions
            .Select(action => action.Opcode)
            .ToArray();
        Array.IndexOf(opcodes, ActionOpcode.CallFunction).ShouldBeLessThan(
            Array.IndexOf(opcodes, ActionOpcode.Delete2));
        Array.IndexOf(opcodes, ActionOpcode.Delete2).ShouldBeLessThan(
            Array.IndexOf(opcodes, ActionOpcode.Return));
    }

    [Fact]
    public void Delete_computed_member_evaluates_receiver_then_key_once()
    {
        var method = CreateComputedMemberDeleteReturn();

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var invokes = artifact.Mir.Instructions
            .Where(instruction =>
                instruction.Kind is Avm1MirInstructionKind.Invoke)
            .ToArray();
        invokes.Length.ShouldBe(2);
        var delete = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.DeleteLValue);
        var lValue = artifact.Mir[delete.LValue];
        lValue.Kind.ShouldBe(Avm1MirLValueKind.Member);
        lValue.Receiver.ShouldBe(invokes[0].Result);
        lValue.Key.ShouldBe(invokes[1].Result);

        var actions = artifact.ActionBody.ShouldNotBeNull().Actions;
        var callIndices = actions
            .Select((action, index) => (action, index))
            .Where(item => item.action is ActionCallFunction)
            .Select(item => item.index)
            .ToArray();
        callIndices.Length.ShouldBe(2);
        var deleteIndex = actions
            .Select((action, index) => (action, index))
            .Single(item => item.action is ActionDelete)
            .index;
        callIndices[0].ShouldBeLessThan(callIndices[1]);
        callIndices[1].ShouldBeLessThan(deleteIndex);
    }

    [Fact]
    public void Rejects_delete_of_a_non_lvalue_before_stack_lowering()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 12, 7, 15);
        var delete = builder.AddExpression(
            Avm1SourceExpressionKind.Delete,
            children: [AddIntegerLiteral(builder, 1)],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: delete);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains("Delete target", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_reports_its_swf5_capability_at_the_source_origin()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 13, 0, 14);
        var delete = builder.AddExpression(
            Avm1SourceExpressionKind.Delete,
            children: [AddDynamicName(builder, "temporary")],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: delete);

        var artifact = Compile(CreateMethod(builder, [statement]), swfVersion: 4);

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains("Delete2 requires SWF 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Member_assignment_statement_preserves_order_without_a_temporary()
    {
        var builder = new Avm1SourceArena.Builder();
        var receiver = builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString("object"));
        var memberName = builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString("key"));
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [receiver, memberName]);
        var value = builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString("rhs"));
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [member, value]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.StoreLValue
        ]);
        var store = artifact.Mir.Instructions[^1];
        store.Result.IsValid.ShouldBeFalse();
        store.Effects.ShouldBe(
            Avm1MirEffect.WritesHeap |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow);
        var lValue = artifact.Mir[store.LValue];
        lValue.Kind.ShouldBe(Avm1MirLValueKind.Member);
        lValue.Receiver.ShouldBe(artifact.Mir.Instructions[0].Result);
        lValue.Key.ShouldBe(artifact.Mir.Instructions[1].Result);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.SetMember
            ]);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBeEmpty();

        var machine = new Avm1Machine(swfVersion: 7);
        var target = new Avm1Object();
        machine.Globals["object"] = target;
        machine.Globals["key"] = new Avm1String("value");
        machine.Globals["rhs"] = new Avm1Number(9);
        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
        target["value"].AsNumber.ShouldBe(9d);
    }

    [Fact]
    public void Member_read_uses_a_structural_lvalue_and_get_member()
    {
        var builder = new Avm1SourceArena.Builder();
        var receiver = builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString("object"));
        var key = builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString("key"));
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [receiver, key]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: member);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Select(instruction => instruction.Kind).ShouldBe([
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.LoadLValue,
            Avm1MirInstructionKind.Return
        ]);
        var load = artifact.Mir.Instructions[2];
        load.Effects.ShouldBe(
            Avm1MirEffect.ReadsHeap |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow);
        var lValue = artifact.Mir[load.LValue];
        lValue.Kind.ShouldBe(Avm1MirLValueKind.Member);
        lValue.Receiver.ShouldBe(artifact.Mir.Instructions[0].Result);
        lValue.Key.ShouldBe(artifact.Mir.Instructions[1].Result);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.GetMember,
                ActionOpcode.Return
            ]);
        var roundTrip = DecompileBack(artifact);
        roundTrip.ShouldBe("return object[key];" + Environment.NewLine);
    }

    [Fact]
    public void Member_assignment_result_spills_with_an_empty_register_file()
    {
        var (method, _) = CreateMemberAssignmentReturn();

        var artifact = Compile(
            method,
            CreateFunction2Options(registerCount: 0, temporaryCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBeEmpty();
        AssertActivationSpills(artifact, expectedCount: 1);
        var target = new Avm1Object();
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["object"] = target;

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        target["value"].AsNumber.ShouldBe(1d);
        machine.ReturnValue.AsNumber.ShouldBe(1d);
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(method, projected);
        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(equivalence) + Environment.NewLine +
            $"expected:{Environment.NewLine}{method.GetAs2Text()}" +
            $"actual:{Environment.NewLine}{projected.GetAs2Text()}");
    }

    [Fact]
    public void Member_assignment_result_uses_a_virtual_private_temporary()
    {
        var (method, assignmentOrigin) = CreateMemberAssignmentReturn();
        var options = new Avm1CompilationOptions(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = 4,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(3, 1)
        };

        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([3]);
        artifact.StackLir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1StackLirInstructionKind.StoreTemporary);
        artifact.StackLir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1StackLirInstructionKind.PushTemporary);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.StoreRegister,
                ActionOpcode.SetMember,
                ActionOpcode.Push,
                ActionOpcode.Return
            ]);
        artifact.ActionBody.Actions[4].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)3);
        artifact.ActionBody.Actions[6].ShouldBeOfType<ActionPush>()
            .PushValues.ShouldHaveSingleItem()
            .ShouldBe(PushValue.Register(3));
        artifact.InstructionMap.Count(mapping =>
            mapping.Origin == assignmentOrigin).ShouldBe(3);
        artifact.StackLir.MaximumStackDepth.ShouldBe(3);
        artifact.ActionBody.MaximumStackDepth.ShouldBe(3);
    }

    [Fact]
    public void Nested_member_assignment_evaluates_each_address_once()
    {
        var builder = new Avm1SourceArena.Builder();
        var outerObject = AddDynamicName(builder, "outer");
        var outerKey = AddDynamicName(builder, "outerKey");
        var innerObject = AddDynamicName(builder, "inner");
        var innerKey = AddDynamicName(builder, "innerKey");
        var rhs = AddDynamicName(builder, "rhs");
        var innerMember = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [innerObject, innerKey]);
        var innerAssignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [innerMember, rhs]);
        var outerMember = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [outerObject, outerKey]);
        var outerAssignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [outerMember, innerAssignment]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: outerAssignment);
        var options = new Avm1CompilationOptions(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = 3,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(2, 1)
        };

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([2]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.StoreRegister,
                ActionOpcode.SetMember,
                ActionOpcode.Push,
                ActionOpcode.SetMember
            ]);

        var machine = new Avm1Machine(swfVersion: 7);
        var outer = new Avm1Object();
        var inner = new Avm1Object();
        machine.Globals["outer"] = outer;
        machine.Globals["outerKey"] = new Avm1String("a");
        machine.Globals["inner"] = inner;
        machine.Globals["innerKey"] = new Avm1String("b");
        machine.Globals["rhs"] = new Avm1Number(11);
        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
        outer["a"].AsNumber.ShouldBe(11d);
        inner["b"].AsNumber.ShouldBe(11d);
    }

    [Fact]
    public void Member_postfix_captures_the_address_and_returns_the_old_value()
    {
        var builder = new Avm1SourceArena.Builder();
        var indexStep = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(builder, "index")]);
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [AddDynamicName(builder, "array"), indexStep]);
        var memberStep = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [member]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: memberStep);

        var artifact = Compile(
            CreateMethod(builder, [statement]),
            CreateFunction2Options(registerCount: 3, temporaryCount: 3));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(3);
        artifact.Mir.LValues.Count(lValue =>
            lValue.Kind is Avm1MirLValueKind.CapturedMember).ShouldBe(1);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2, 3]);

        var array = new Avm1Object();
        array["0"] = new Avm1String("3");
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["array"] = array;
        machine.Globals["index"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
        machine.Globals["index"].AsNumber.ShouldBe(1d);
        array["0"].AsNumber.ShouldBe(4d);
        machine.ReturnValue.AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void Compound_member_assignment_evaluates_receiver_and_key_once()
    {
        var builder = new Avm1SourceArena.Builder();
        var receiverIndexStep = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(builder, "receiverIndex")]);
        var receiver = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [AddDynamicName(builder, "holder"), receiverIndexStep]);
        var keyIndexStep = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddDynamicName(builder, "keyIndex")]);
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [receiver, keyIndexStep]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children: [member, AddDynamicName(builder, "rhs")]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: assignment);

        var artifact = Compile(
            CreateMethod(builder, [statement]),
            CreateFunction2Options(registerCount: 2, temporaryCount: 2));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(2);
        var captured = artifact.Mir.LValues.Single(lValue =>
            lValue.Kind is Avm1MirLValueKind.CapturedMember);
        captured.ReceiverTemporary.IsValid.ShouldBeTrue();
        captured.KeyTemporary.IsValid.ShouldBeTrue();
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2]);

        var target = new Avm1Object();
        target["0"] = new Avm1Number(10);
        var holder = new Avm1Object();
        holder["0"] = target;
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["holder"] = holder;
        machine.Globals["receiverIndex"] = new Avm1Number(0);
        machine.Globals["keyIndex"] = new Avm1Number(0);
        machine.Globals["rhs"] = new Avm1Number(5);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
        machine.Globals["receiverIndex"].AsNumber.ShouldBe(1d);
        machine.Globals["keyIndex"].AsNumber.ShouldBe(1d);
        target["0"].AsNumber.ShouldBe(15d);
        machine.ReturnValue.AsNumber.ShouldBe(15d);
    }

    [Fact]
    public void Captured_member_address_spills_excess_register_capacity()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 8, 19, 38);
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children:
            [
                AddDynamicName(builder, "object"),
                AddDynamicName(builder, "key")
            ]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children: [member, AddIntegerLiteral(builder, 1)],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);

        var artifact = Compile(
            CreateMethod(builder, [statement]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
        AssertActivationSpills(artifact, expectedCount: 1);
        var target = new Avm1Object();
        target["value"] = new Avm1Number(2);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["object"] = target;
        machine.Globals["key"] = new Avm1String("value");

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        target["value"].AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void Named_zero_argument_call_needs_no_temporary_register()
    {
        var builder = new Avm1SourceArena.Builder();
        var target = AddDynamicName(builder, "fn");
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [target]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: call);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.CallSites.ShouldHaveSingleItem().TargetKind
            .ShouldBe(Avm1MirInvocationTargetKind.Name);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBeEmpty();
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.CallFunction,
                ActionOpcode.Return
            ]);
        GetPushedValue(artifact.ActionBody.Actions[0])
            .ShouldBe(PushValue.Integer(0));
        GetPushedString(artifact.ActionBody.Actions[1]).ShouldBe("fn");
        DecompileBack(artifact).ShouldContain("return fn();");
    }

    [Fact]
    public void Named_call_preserves_left_to_right_argument_evaluation()
    {
        var builder = new Avm1SourceArena.Builder();
        var target = AddDynamicName(builder, "fn");
        var left = AddDynamicName(builder, "left");
        var right = AddDynamicName(builder, "right");
        var callOrigin = builder.AddOrigin(0, 4, 7, 22);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [target, left, right],
            origin: callOrigin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: call);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var invoke = artifact.Mir.Instructions
            .Single(instruction => instruction.Kind is Avm1MirInstructionKind.Invoke);
        invoke.Effects.ShouldBe(
            Avm1MirEffect.ReadsDynamicScope |
            Avm1MirEffect.WritesDynamicScope |
            Avm1MirEffect.ReadsHeap |
            Avm1MirEffect.WritesHeap |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow);
        var callSite = artifact.Mir[invoke.CallSite];
        callSite.TargetKind.ShouldBe(Avm1MirInvocationTargetKind.Name);
        callSite.Arguments.Count.ShouldBe(2);
        artifact.Mir.GetCallArgument(callSite, 0)
            .ShouldBe(artifact.Mir.Instructions[0].Result);
        artifact.Mir.GetCallArgument(callSite, 1)
            .ShouldBe(artifact.Mir.Instructions[1].Result);

        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.StoreRegister,
                ActionOpcode.Pop,
                ActionOpcode.StoreRegister,
                ActionOpcode.Pop,
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.CallFunction,
                ActionOpcode.Return
            ]);
        GetPushedString(artifact.ActionBody.Actions[0]).ShouldBe("left");
        GetPushedString(artifact.ActionBody.Actions[2]).ShouldBe("right");
        artifact.ActionBody.Actions[4].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)2);
        artifact.ActionBody.Actions[6].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)1);
        GetPushedValue(artifact.ActionBody.Actions[8])
            .ShouldBe(PushValue.Register(2));
        GetPushedValue(artifact.ActionBody.Actions[9])
            .ShouldBe(PushValue.Register(1));
        GetPushedValue(artifact.ActionBody.Actions[10])
            .ShouldBe(PushValue.Integer(2));
        GetPushedString(artifact.ActionBody.Actions[11]).ShouldBe("fn");
        artifact.StackLir.MaximumStackDepth.ShouldBe(4);
        artifact.ActionBody.MaximumStackDepth.ShouldBe(4);
    }

    [Fact]
    public void Computed_method_call_preserves_receiver_key_and_argument_order()
    {
        var builder = new Avm1SourceArena.Builder();
        var receiver = AddDynamicName(builder, "receiver");
        var key = AddDynamicName(builder, "key");
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [receiver, key]);
        var first = AddDynamicName(builder, "first");
        var second = AddDynamicName(builder, "second");
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [member, first, second]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: call);
        var options = CreateFunction2Options(registerCount: 4, temporaryCount: 4);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.CallSites.ShouldHaveSingleItem().TargetKind
            .ShouldBe(Avm1MirInvocationTargetKind.Member);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2, 3, 4]);
        artifact.ActionBody.ShouldNotBeNull().Actions[21]
            .ShouldBeOfType<ActionCallMethod>();
        GetPushedString(artifact.ActionBody.Actions[0]).ShouldBe("receiver");
        GetPushedString(artifact.ActionBody.Actions[2]).ShouldBe("key");
        GetPushedString(artifact.ActionBody.Actions[4]).ShouldBe("first");
        GetPushedString(artifact.ActionBody.Actions[6]).ShouldBe("second");
        artifact.ActionBody.Actions[8].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)4);
        artifact.ActionBody.Actions[10].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)3);
        artifact.ActionBody.Actions[12].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)2);
        artifact.ActionBody.Actions[14].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)1);
        GetPushedValue(artifact.ActionBody.Actions[16])
            .ShouldBe(PushValue.Register(4));
        GetPushedValue(artifact.ActionBody.Actions[17])
            .ShouldBe(PushValue.Register(3));
        GetPushedValue(artifact.ActionBody.Actions[18])
            .ShouldBe(PushValue.Integer(2));
        GetPushedValue(artifact.ActionBody.Actions[19])
            .ShouldBe(PushValue.Register(1));
        GetPushedValue(artifact.ActionBody.Actions[20])
            .ShouldBe(PushValue.Register(2));
        artifact.StackLir.MaximumStackDepth.ShouldBe(5);
        artifact.ActionBody.MaximumStackDepth.ShouldBe(5);
    }

    [Fact]
    public void Named_construction_selects_new_object_and_records_allocation()
    {
        var builder = new Avm1SourceArena.Builder();
        var target = builder.AddExpression(
            Avm1SourceExpressionKind.QualifiedName,
            name: builder.InternString("pkg.Widget"));
        var construction = builder.AddExpression(
            Avm1SourceExpressionKind.New,
            children: [target]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: construction);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var invoke = artifact.Mir.Instructions
            .Single(instruction => instruction.Kind is Avm1MirInstructionKind.Invoke);
        artifact.Mir[invoke.CallSite].Kind.ShouldBe(Avm1MirInvocationKind.Construct);
        invoke.Effects.ShouldBe(
            Avm1MirEffect.ReadsDynamicScope |
            Avm1MirEffect.WritesDynamicScope |
            Avm1MirEffect.ReadsHeap |
            Avm1MirEffect.WritesHeap |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow |
            Avm1MirEffect.Allocates);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBeEmpty();
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.NewObject,
                ActionOpcode.Return
            ]);
        GetPushedString(artifact.ActionBody.Actions[1]).ShouldBe("pkg.Widget");
        DecompileBack(artifact).ShouldContain("return new pkg.Widget();");
    }

    [Fact]
    public void Computed_construction_selects_new_method()
    {
        var builder = new Avm1SourceArena.Builder();
        var receiver = AddDynamicName(builder, "factory");
        var key = AddDynamicName(builder, "typeName");
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [receiver, key]);
        var construction = builder.AddExpression(
            Avm1SourceExpressionKind.New,
            children: [member]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: construction);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.CallSites.ShouldHaveSingleItem().Kind
            .ShouldBe(Avm1MirInvocationKind.Construct);
        artifact.ActionBody.ShouldNotBeNull().Actions[11]
            .ShouldBeOfType<ActionNewMethod>();
        GetPushedValue(artifact.ActionBody.Actions[8])
            .ShouldBe(PushValue.Integer(0));
        GetPushedValue(artifact.ActionBody.Actions[9])
            .ShouldBe(PushValue.Register(1));
        GetPushedValue(artifact.ActionBody.Actions[10])
            .ShouldBe(PushValue.Register(2));
        artifact.ActionBody.MaximumStackDepth.ShouldBe(3);
    }

    [Fact]
    public void Invocation_spills_excess_temporary_capacity()
    {
        var builder = new Avm1SourceArena.Builder();
        var target = AddDynamicName(builder, "fn");
        var first = AddDynamicName(builder, "first");
        var second = AddDynamicName(builder, "second");
        var callOrigin = builder.AddOrigin(0, 2, 4, 21);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [target, first, second],
            origin: callOrigin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: call);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
        AssertActivationSpills(artifact, expectedCount: 1);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action is ActionCallFunction);
    }

    [Fact]
    public void Logical_statement_condition_does_not_hold_a_phi_during_invocation()
    {
        var builder = new Avm1SourceArena.Builder();
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children:
            [
                AddDynamicName(builder, "receiver"),
                AddStringLiteral(builder, "handleInput")
            ]);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                member,
                AddDynamicName(builder, "first"),
                AddDynamicName(builder, "second")
            ]);
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalOr,
            children: [AddDynamicName(builder, "skip"), call]);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [builder.AddStatement(Avm1SourceStatementKind.Return)]);
        var conditional = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: condition,
            children: [body]);
        var evaluate = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "eval"),
                AddStringLiteral(builder, "dynamicName")
            ]);
        var artifact = Compile(CreateMethod(
            builder,
            [
                conditional,
                builder.AddStatement(
                    Avm1SourceStatementKind.Expression,
                    expression: evaluate)
            ]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var closureAnalysis = artifact.ClosureAnalysis.ShouldNotBeNull();
        closureAnalysis[closureAnalysis.RootCodeUnit].Flags.HasFlag(
                Avm1ClosureCodeUnitFlags.InvokesEval).ShouldBeTrue();
        artifact.Mir.PhiSites.ShouldBeEmpty();
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([
            (byte)0,
            (byte)1,
            (byte)2,
            (byte)3
        ]);
        artifact.StackLir.TemporaryStorages.ShouldAllBe(storage =>
            storage.Kind == Avm1StackLirTemporaryStorageKind.Register);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action is ActionCallMethod);
    }

    [Fact]
    public void Adobe_member_invocation_keeps_reverse_arguments_on_the_stack()
    {
        var builder = new Avm1SourceArena.Builder();
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children:
            [
                AddDynamicName(builder, "receiver"),
                AddStringLiteral(builder, "invoke")
            ]);
        var children = new List<SourceExpressionIndex> { member };
        children.AddRange(Enumerable.Range(0, 6).Select(index =>
            AddDynamicName(builder, $"argument{index}")));
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: children);
        var evaluate = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "eval"),
                AddStringLiteral(builder, "dynamicName")
            ]);
        var artifact = Compile(
            CreateMethod(
                builder,
                [
                    builder.AddStatement(
                        Avm1SourceStatementKind.Expression,
                        expression: call),
                    builder.AddStatement(
                        Avm1SourceStatementKind.Expression,
                        expression: evaluate)
                ]),
            new Avm1CompilationOptions(7)
            {
                ExpressionEvaluationMode =
                    Avm1ExpressionEvaluationMode.AdobeFlashCs6Compatible
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var callSite = artifact.Mir.CallSites.ShouldHaveSingleItem();
        callSite.ArgumentEvaluationOrder.ShouldBe(
            Avm1MirEvaluationOrder.Reverse);
        callSite.TargetEvaluationOrder.ShouldBe(
            Avm1MirInvocationTargetOrder.AfterArguments);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([
            (byte)0,
            (byte)1
        ]);
        artifact.StackLir.TemporaryStorages.ShouldAllBe(storage =>
            storage.Kind == Avm1StackLirTemporaryStorageKind.Register);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action is ActionCallMethod);
    }

    [Fact]
    public void Direct_eval_requires_no_call_transport_or_activation_spills()
    {
        var builder = new Avm1SourceArena.Builder();
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "eval"),
                AddStringLiteral(builder, "value")
            ]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: call);

        var artifact = Compile(
            CreateMethod(builder, [statement]),
            CreateFunction2Options(registerCount: 0, temporaryCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var closureAnalysis = artifact.ClosureAnalysis.ShouldNotBeNull();
        closureAnalysis[closureAnalysis.RootCodeUnit].Flags.HasFlag(
                Avm1ClosureCodeUnitFlags.InvokesEval).ShouldBeTrue();
        artifact.Mir.CallSites.ShouldBeEmpty();
        var evaluateName = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.EvaluateName);
        evaluateName.Effects.ShouldBe(
            Avm1MirEffect.ReadsActivation |
            Avm1MirEffect.ReadsDynamicScope |
            Avm1MirEffect.ReadsHeap |
            Avm1MirEffect.MayInvokeUserCode |
            Avm1MirEffect.MayThrow);
        artifact.StackLir.ShouldNotBeNull().TemporaryStorages.ShouldBeEmpty();
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action is ActionGetVariable);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action is ActionCallFunction);
    }

    [Fact]
    public void Direct_eval_reads_a_named_local_at_runtime()
    {
        var builder = new Avm1SourceArena.Builder();
        var local = AddLocalSymbol(builder, "local");
        var declaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 41),
            symbol: local);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "eval"),
                AddStringLiteral(builder, "local")
            ]);
        var @return = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: call);

        var artifact = Compile(CreateMethod(builder, [declaration, @return]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ClosureAnalysis.ShouldNotBeNull()[local]
            .RequiresNamedActivation.ShouldBeTrue();
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action is ActionGetVariable).ShouldBe(1);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action is ActionCallFunction);
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(41d);
    }

    [Fact]
    public void Computed_dynamic_name_executes_the_same_lookup_contract()
    {
        var builder = new Avm1SourceArena.Builder();
        var evaluated = builder.AddExpression(
            Avm1SourceExpressionKind.ComputedDynamicName,
            children: [AddDynamicName(builder, "name")]);
        var @return = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: evaluated);
        var artifact = Compile(CreateMethod(builder, [@return]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var closureAnalysis = artifact.ClosureAnalysis.ShouldNotBeNull();
        closureAnalysis[closureAnalysis.RootCodeUnit].Flags.HasFlag(
            Avm1ClosureCodeUnitFlags.InvokesEval).ShouldBeTrue();
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.EvaluateName).ShouldBe(1);
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action is ActionGetVariable).ShouldBe(2);
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["name"] = new Avm1String("target");
        machine.Globals["target"] = new Avm1Number(17);

        machine.Execute(artifact.ActionBody.Actions, strict: true);

        machine.ReturnValue.AsNumber.ShouldBe(17d);
        artifact.Source.GetAs2Text().ShouldContain("return eval(name);");
    }

    [Fact]
    public void Direct_eval_is_available_in_the_swf4_backend()
    {
        var builder = new Avm1SourceArena.Builder();
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "eval"),
                AddStringLiteral(builder, "value")
            ]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: call);

        var artifact = Compile(CreateMethod(builder, [statement]), swfVersion: 4);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Pop
            ]);
    }

    [Fact]
    public void Rejects_a_direct_eval_call_without_its_name_argument()
    {
        var builder = new Avm1SourceArena.Builder();
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "eval")]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: call);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Message.Contains(
                "exactly one argument",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Direct_eval_round_trips_as_a_computed_dynamic_name()
    {
        var builder = new Avm1SourceArena.Builder();
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                AddDynamicName(builder, "eval"),
                AddDynamicName(builder, "name")
            ]);
        var source = CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: call)]);
        var artifact = Compile(source);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var projected = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();
        var equivalence = Avm1SourceEquivalence.Compare(source, projected);

        equivalence.Status.ShouldBe(
            Avm1SourceEquivalenceStatus.Equivalent,
            DescribeEquivalence(equivalence));
        projected.GetAs2Text().ShouldContain("return eval(name);");
    }

    [Fact]
    public void Nested_calls_reuse_the_reserved_temporary_range()
    {
        var builder = new Avm1SourceArena.Builder();
        var innerTarget = AddDynamicName(builder, "inner");
        var first = AddDynamicName(builder, "first");
        var innerCall = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [innerTarget, first]);
        var outerTarget = AddDynamicName(builder, "outer");
        var second = AddDynamicName(builder, "second");
        var outerCall = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [outerTarget, innerCall, second]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: outerCall);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.CallSites.Count.ShouldBe(2);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionCallFunction>().Count().ShouldBe(2);
        artifact.ActionBody.Actions.OfType<ActionStoreRegister>()
            .Select(action => action.RegisterNumber).ShouldBe([1, 2, 1]);
    }

    [Fact]
    public void Value_target_invocation_uses_blank_method_forms()
    {
        (Avm1SourceExpressionKind SourceKind, Avm1MirInvocationKind MirKind,
            ActionOpcode Opcode)[] cases =
        [
            (Avm1SourceExpressionKind.Call,
                Avm1MirInvocationKind.Call,
                ActionOpcode.CallMethod),
            (Avm1SourceExpressionKind.New,
                Avm1MirInvocationKind.Construct,
                ActionOpcode.NewMethod)
        ];

        foreach (var (sourceKind, mirKind, opcode) in cases)
        {
            var builder = new Avm1SourceArena.Builder();
            var factory = AddDynamicName(builder, "factory");
            var factoryCall = builder.AddExpression(
                Avm1SourceExpressionKind.Call,
                children: [factory]);
            var argument = AddDynamicName(builder, "argument");
            var invocation = builder.AddExpression(
                sourceKind,
                children: [factoryCall, argument]);
            var statement = builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: invocation);
            var options = CreateFunction2Options(
                registerCount: 2,
                temporaryCount: 2);

            var artifact = Compile(CreateMethod(builder, [statement]), options);

            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            artifact.Mir.CallSites.Count.ShouldBe(2);
            artifact.Mir.CallSites[1].TargetKind
                .ShouldBe(Avm1MirInvocationTargetKind.Value);
            artifact.Mir.CallSites[1].Kind.ShouldBe(mirKind);
            artifact.ActionBody.ShouldNotBeNull().Actions[13].Opcode
                .ShouldBe(opcode);
            GetPushedValue(artifact.ActionBody.Actions[9])
                .ShouldBe(PushValue.Register(2));
            GetPushedValue(artifact.ActionBody.Actions[10])
                .ShouldBe(PushValue.Integer(1));
            GetPushedValue(artifact.ActionBody.Actions[11])
                .ShouldBe(PushValue.Register(1));
            GetPushedString(artifact.ActionBody.Actions[12])
                .ShouldBe(string.Empty);
        }
    }

    [Fact]
    public void Empty_aggregate_literals_need_no_temporary_registers()
    {
        (Avm1SourceExpressionKind Kind, Avm1MirAggregateKind MirKind,
            Avm1MirEffect Effects, ActionOpcode Opcode, string Text)[] cases =
        [
            (Avm1SourceExpressionKind.ArrayLiteral,
                Avm1MirAggregateKind.Array,
                Avm1MirEffect.Allocates,
                ActionOpcode.InitArray,
                "return [];"),
            (Avm1SourceExpressionKind.ObjectLiteral,
                Avm1MirAggregateKind.Object,
                Avm1MirEffect.Allocates,
                ActionOpcode.InitObject,
                "return {};")
        ];

        foreach (var (kind, mirKind, effects, opcode, text) in cases)
        {
            var builder = new Avm1SourceArena.Builder();
            var literal = builder.AddExpression(kind);
            var statement = builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: literal);

            var artifact = Compile(CreateMethod(builder, [statement]));

            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            var aggregate = artifact.Mir.Instructions
                .Single(instruction =>
                    instruction.Kind is Avm1MirInstructionKind.Aggregate);
            aggregate.Effects.ShouldBe(effects);
            artifact.Mir[aggregate.AggregateSite].Kind.ShouldBe(mirKind);
            artifact.Mir[aggregate.AggregateSite].Values.Count.ShouldBe(0);
            artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBeEmpty();
            artifact.ActionBody.ShouldNotBeNull().Actions
                .Select(action => action.Opcode).ShouldBe([
                    ActionOpcode.Push,
                    opcode,
                    ActionOpcode.Return
                ]);
            GetPushedValue(artifact.ActionBody.Actions[0])
                .ShouldBe(PushValue.Integer(0));
            artifact.ActionBody.MaximumStackDepth.ShouldBe(1);
            DecompileBack(artifact).ShouldContain(text);
        }
    }

    [Fact]
    public void Array_literal_preserves_left_to_right_evaluation_and_element_order()
    {
        var builder = new Avm1SourceArena.Builder();
        var first = AddDynamicName(builder, "first");
        var second = AddDynamicName(builder, "second");
        var third = AddDynamicName(builder, "third");
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children: [first, second, third]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: literal);
        var options = CreateFunction2Options(registerCount: 3, temporaryCount: 3);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var aggregate = artifact.Mir.AggregateSites.ShouldHaveSingleItem();
        aggregate.Kind.ShouldBe(Avm1MirAggregateKind.Array);
        artifact.Mir.GetAggregateValue(aggregate, 0)
            .ShouldBe(artifact.Mir.Instructions[0].Result);
        artifact.Mir.GetAggregateValue(aggregate, 1)
            .ShouldBe(artifact.Mir.Instructions[1].Result);
        artifact.Mir.GetAggregateValue(aggregate, 2)
            .ShouldBe(artifact.Mir.Instructions[2].Result);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2, 3]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode).ShouldBe([
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.Push,
                ActionOpcode.GetVariable,
                ActionOpcode.StoreRegister,
                ActionOpcode.Pop,
                ActionOpcode.StoreRegister,
                ActionOpcode.Pop,
                ActionOpcode.StoreRegister,
                ActionOpcode.Pop,
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.Push,
                ActionOpcode.InitArray,
                ActionOpcode.Return
            ]);
        GetPushedString(artifact.ActionBody.Actions[0]).ShouldBe("first");
        GetPushedString(artifact.ActionBody.Actions[2]).ShouldBe("second");
        GetPushedString(artifact.ActionBody.Actions[4]).ShouldBe("third");
        artifact.ActionBody.Actions[6].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)3);
        artifact.ActionBody.Actions[8].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)2);
        artifact.ActionBody.Actions[10].ShouldBeOfType<ActionStoreRegister>()
            .RegisterNumber.ShouldBe((byte)1);
        GetPushedValue(artifact.ActionBody.Actions[12])
            .ShouldBe(PushValue.Register(3));
        GetPushedValue(artifact.ActionBody.Actions[13])
            .ShouldBe(PushValue.Register(2));
        GetPushedValue(artifact.ActionBody.Actions[14])
            .ShouldBe(PushValue.Register(1));
        GetPushedValue(artifact.ActionBody.Actions[15])
            .ShouldBe(PushValue.Integer(3));
        artifact.ActionBody.MaximumStackDepth.ShouldBe(4);
        var roundTrip = DecompileBack(artifact);
        roundTrip.ShouldContain("return [");
        roundTrip.IndexOf("first", StringComparison.Ordinal)
            .ShouldBeLessThan(roundTrip.IndexOf("second", StringComparison.Ordinal));
        roundTrip.IndexOf("second", StringComparison.Ordinal)
            .ShouldBeLessThan(roundTrip.IndexOf("third", StringComparison.Ordinal));
    }

    [Fact]
    public void Object_literal_preserves_key_value_evaluation_and_runtime_order()
    {
        var builder = new Avm1SourceArena.Builder();
        var firstKey = AddStringLiteral(builder, "first");
        var firstValue = AddDynamicName(builder, "firstValue");
        var secondKey = AddStringLiteral(builder, "second");
        var secondValue = AddDynamicName(builder, "secondValue");
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children: [firstKey, firstValue, secondKey, secondValue]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: literal);
        var options = CreateFunction2Options(registerCount: 4, temporaryCount: 4);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var aggregate = artifact.Mir.AggregateSites.ShouldHaveSingleItem();
        aggregate.Kind.ShouldBe(Avm1MirAggregateKind.Object);
        aggregate.Values.Count.ShouldBe(4);
        artifact.Mir.Instructions
            .Single(instruction =>
                instruction.Kind is Avm1MirInstructionKind.Aggregate)
            .Effects.ShouldBe(Avm1MirEffect.Allocates);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2, 3, 4]);
        GetPushedString(artifact.ActionBody.ShouldNotBeNull().Actions[0])
            .ShouldBe("first");
        GetPushedString(artifact.ActionBody.Actions[3]).ShouldBe("second");
        GetPushedValue(artifact.ActionBody.Actions[14])
            .ShouldBe(PushValue.Register(1));
        GetPushedValue(artifact.ActionBody.Actions[15])
            .ShouldBe(PushValue.Register(2));
        GetPushedValue(artifact.ActionBody.Actions[16])
            .ShouldBe(PushValue.Register(3));
        GetPushedValue(artifact.ActionBody.Actions[17])
            .ShouldBe(PushValue.Register(4));
        GetPushedValue(artifact.ActionBody.Actions[18])
            .ShouldBe(PushValue.Integer(2));
        artifact.ActionBody.Actions[19].ShouldBeOfType<ActionInitObject>();
        artifact.ActionBody.MaximumStackDepth.ShouldBe(5);
        var roundTrip = DecompileBack(artifact);
        roundTrip.ShouldContain("return {");
        roundTrip.IndexOf("first:", StringComparison.Ordinal).ShouldBeLessThan(
            roundTrip.IndexOf("second:", StringComparison.Ordinal));
        roundTrip.IndexOf("firstValue", StringComparison.Ordinal).ShouldBeLessThan(
            roundTrip.IndexOf("secondValue", StringComparison.Ordinal));
    }

    [Fact]
    public void Aggregate_literals_execute_with_source_values()
    {
        var builder = new Avm1SourceArena.Builder();
        var arrayTarget = AddDynamicName(builder, "arrayResult");
        var first = AddDynamicName(builder, "first");
        var second = AddDynamicName(builder, "second");
        var array = builder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children: [first, second]);
        var arrayAssignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [arrayTarget, array]);
        var arrayStatement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: arrayAssignment);

        var objectTarget = AddDynamicName(builder, "objectResult");
        var objectLiteral = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddStringLiteral(builder, "a"),
                AddDynamicName(builder, "first"),
                AddStringLiteral(builder, "b"),
                AddDynamicName(builder, "second")
            ]);
        var objectAssignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [objectTarget, objectLiteral]);
        var objectStatement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: objectAssignment);
        var options = CreateFunction2Options(registerCount: 4, temporaryCount: 4);
        var artifact = Compile(
            CreateMethod(builder, [arrayStatement, objectStatement]),
            options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["first"] = new Avm1Number(1);
        machine.Globals["second"] = new Avm1Number(2);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
        var arrayResult = machine.Globals["arrayResult"].ShouldBeOfType<Avm1Array>();
        arrayResult.Items.Select(item => item.AsNumber).ShouldBe([1d, 2d]);
        var objectResult = machine.Globals["objectResult"].ShouldBeOfType<Avm1Object>();
        objectResult["a"].AsNumber.ShouldBe(1d);
        objectResult["b"].AsNumber.ShouldBe(2d);
    }

    [Fact]
    public void Nested_aggregates_reuse_the_reserved_temporary_range()
    {
        var builder = new Avm1SourceArena.Builder();
        var inner = builder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children: [AddDynamicName(builder, "first")]);
        var outer = builder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children: [inner, AddDynamicName(builder, "second")]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: outer);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.AggregateSites.Count.ShouldBe(2);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1, 2]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionStoreRegister>()
            .Select(action => action.RegisterNumber).ShouldBe([1, 2, 1]);
        var roundTrip = DecompileBack(artifact);
        roundTrip.ShouldContain("return [");
        roundTrip.ShouldContain("= [");
        roundTrip.IndexOf("first", StringComparison.Ordinal)
            .ShouldBeLessThan(roundTrip.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void Aggregate_evaluates_side_effecting_elements_left_to_right()
    {
        var builder = new Avm1SourceArena.Builder();
        var firstAssignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children:
            [
                AddDynamicName(builder, "order"),
                AddIntegerLiteral(builder, 1)
            ]);
        var secondAssignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children:
            [
                AddDynamicName(builder, "order"),
                AddIntegerLiteral(builder, 2)
            ]);
        var array = builder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children: [firstAssignment, secondAssignment]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), array]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);
        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["order"].AsNumber.ShouldBe(2d);
        var result = machine.Globals["result"].ShouldBeOfType<Avm1Array>();
        result.Items.Select(item => item.AsNumber).ShouldBe([1d, 2d]);
    }

    [Fact]
    public void Object_literal_duplicate_keys_match_Adobe_first_source_value_semantics()
    {
        var builder = new Avm1SourceArena.Builder();
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddStringLiteral(builder, "same"),
                AddIntegerLiteral(builder, 1),
                AddStringLiteral(builder, "same"),
                AddIntegerLiteral(builder, 2)
            ]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), literal]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var options = CreateFunction2Options(registerCount: 4, temporaryCount: 4);
        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["result"].ShouldBeOfType<Avm1Object>()["same"]
            .AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void Dynamic_object_key_keeps_the_conversion_effect_barrier()
    {
        var builder = new Avm1SourceArena.Builder();
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddDynamicName(builder, "key"),
                AddIntegerLiteral(builder, 1)
            ]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: literal);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions
            .Single(instruction =>
                instruction.Kind is Avm1MirInstructionKind.Aggregate)
            .Effects.ShouldBe(
                Avm1MirEffect.Allocates |
                Avm1MirEffect.ReadsHeap |
                Avm1MirEffect.MayInvokeUserCode |
                Avm1MirEffect.MayThrow);
    }

    [Fact]
    public void Aggregate_spills_excess_temporary_capacity()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 3, 7, 22);
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.ArrayLiteral,
            children:
            [
                AddDynamicName(builder, "first"),
                AddDynamicName(builder, "second")
            ],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: literal);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);

        var artifact = Compile(CreateMethod(builder, [statement]), options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
        AssertActivationSpills(artifact, expectedCount: 1);
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldContain(action =>
            action.Opcode == ActionOpcode.InitArray);
    }

    [Fact]
    public void Rejects_object_literal_with_an_odd_child_count()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 1, 4, 14);
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children: [AddStringLiteral(builder, "orphan")],
            origin: origin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: literal);

        var artifact = Compile(CreateMethod(builder, [statement]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains(
                "alternating key and value",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_invalid_register_plans_before_mir_lowering()
    {
        var builder = new Avm1SourceArena.Builder();
        var method = CreateMethod(builder, []);
        (Avm1CompilationOptions Options, string Message)[] cases =
        [
            (new Avm1CompilationOptions(7) { RegisterCount = 3 },
                "RegisterCount must be zero"),
            (new Avm1CompilationOptions(7)
            {
                ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(4, 1)
            },
                "outside the 0..3 range"),
            (new Avm1CompilationOptions(6)
            {
                RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
                RegisterCount = 1
            }, "requires target SWF 7"),
            (new Avm1CompilationOptions(7)
            {
                RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
                RegisterCount = 2,
                ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(3, 1)
            }, "outside the 0..1 range")
        ];

        foreach (var (options, message) in cases)
        {
            var artifact = Compile(method, options);

            artifact.Succeeded.ShouldBeFalse();
            artifact.Mir.Instructions.ShouldBeEmpty();
            artifact.Diagnostics.ShouldContain(diagnostic =>
                diagnostic.Code == "AVM1CMP010" &&
                diagnostic.Message.Contains(message, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Refuses_incomplete_source_hir_without_lowering_it()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 0);
        var body = builder.AddStatement(Avm1SourceStatementKind.Block);
        var method = new Avm1SourceMethod(
            builder.ToArena(),
            body,
            [],
            [
                new Avm1SourceDiagnostic(
                    "AVM1SRC004",
                    Avm1SourceDiagnosticSeverity.Error,
                    origin,
                    "Unsupported action.")
            ]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeFalse();
        artifact.Mir.Instructions.ShouldBeEmpty();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP001" && diagnostic.Origin == origin);
    }

    [Fact]
    public void Preserves_source_warning_without_failing_compilation()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 0);
        var body = builder.AddStatement(Avm1SourceStatementKind.Block);
        var method = new Avm1SourceMethod(
            builder.ToArena(),
            body,
            [],
            [
                new Avm1SourceDiagnostic(
                    "AVM1SRC005",
                    Avm1SourceDiagnosticSeverity.Warning,
                    origin,
                    "Conservative recovery warning.")
            ]);

        var artifact = Compile(method);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP000" &&
            diagnostic.Severity == Avm1CompilationDiagnosticSeverity.Warning &&
            diagnostic.Origin == origin);
    }

    [Fact]
    public void Maps_target_version_failure_back_to_source_origins()
    {
        var builder = new Avm1SourceArena.Builder();
        var expressionOrigin = builder.AddOrigin(0, 0, 7, 8);
        var returnOrigin = builder.AddOrigin(0, 1, 0, 9);
        var value = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1),
            origin: expressionOrigin);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: value,
            origin: returnOrigin);
        var method = CreateMethod(builder, [statement]);

        var artifact = Compile(method, swfVersion: 4);

        artifact.Succeeded.ShouldBeFalse();
        artifact.ActionBody.ShouldNotBeNull().Bytes.IsEmpty.ShouldBeTrue();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.Origin == expressionOrigin &&
            diagnostic.Message.Contains("integer value form", StringComparison.Ordinal));
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1VER019" &&
            diagnostic.Origin == returnOrigin &&
            diagnostic.Message.Contains("Return requires SWF 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_method_compiles_to_an_empty_verified_body()
    {
        var builder = new Avm1SourceArena.Builder();
        var artifact = Compile(CreateMethod(builder, []));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Bytecode.IsEmpty.ShouldBeTrue();
        artifact.ActionBody.ShouldNotBeNull().Actions.ShouldBeEmpty();
        artifact.StackLir.ShouldNotBeNull().MaximumStackDepth.ShouldBe(0);
    }

    [Fact]
    public void Decompiled_source_hir_round_trips_through_the_method_backend()
    {
        var source = Avm1Decompiler.DecompileMethod(
            [
                new ActionPush([
                    PushValue.String("value"),
                    PushValue.Integer(3)
                ]),
                new ActionDefineLocal(),
                new ActionPush([PushValue.String("value")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.Integer(2)]),
                new ActionAdd2(),
                new ActionReturn()
            ],
            swfVersion: 7).ProjectSource();

        var artifact = Compile(source);
        var roundTrip = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        roundTrip.GetAs2Text().ShouldBe(source.GetAs2Text());
    }

    [Fact]
    public void Decompiled_member_operations_round_trip_through_source_hir()
    {
        ShockwaveFlash.Avm1.Action[][] cases =
        [
            [
                new ActionPush([PushValue.String("object")]),
                new ActionGetVariable(),
                new ActionPush([
                    PushValue.String("value"),
                    PushValue.Integer(9)
                ]),
                new ActionSetMember()
            ],
            [
                new ActionPush([PushValue.String("object")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("value")]),
                new ActionGetMember(),
                new ActionReturn()
            ]
        ];

        foreach (var actions in cases)
        {
            var source = Avm1Decompiler.DecompileMethod(
                actions,
                swfVersion: 7).ProjectSource();

            var artifact = Compile(source);
            var roundTrip = Avm1Decompiler.DecompileMethod(
                artifact.ActionBody.ShouldNotBeNull().Actions,
                swfVersion: 7).ProjectSource();

            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            roundTrip.GetAs2Text().ShouldBe(source.GetAs2Text());
        }
    }

    [Fact]
    public void Decompiled_invocations_round_trip_through_source_hir()
    {
        ShockwaveFlash.Avm1.Action[][] cases =
        [
            [
                new ActionPush([
                    PushValue.String("right"),
                    PushValue.String("left"),
                    PushValue.Integer(2),
                    PushValue.String("fn")
                ]),
                new ActionCallFunction(),
                new ActionReturn()
            ],
            [
                new ActionPush([
                    PushValue.String("right"),
                    PushValue.String("left"),
                    PushValue.Integer(2),
                    PushValue.String("pkg.Widget")
                ]),
                new ActionNewObject(),
                new ActionReturn()
            ],
            [
                new ActionPush([
                    PushValue.String("right"),
                    PushValue.String("left"),
                    PushValue.Integer(2),
                    PushValue.String("object"),
                    PushValue.String("method")
                ]),
                new ActionCallMethod(),
                new ActionReturn()
            ],
            [
                new ActionPush([
                    PushValue.String("right"),
                    PushValue.String("left"),
                    PushValue.Integer(2),
                    PushValue.String("factory"),
                    PushValue.String("Widget")
                ]),
                new ActionNewMethod(),
                new ActionReturn()
            ]
        ];
        var options = CreateFunction2Options(registerCount: 4, temporaryCount: 4);

        foreach (var actions in cases)
        {
            var source = Avm1Decompiler.DecompileMethod(
                actions,
                swfVersion: 7).ProjectSource();

            var artifact = Compile(source, options);
            var roundTrip = Avm1Decompiler.DecompileMethod(
                artifact.ActionBody.ShouldNotBeNull().Actions,
                swfVersion: 7).ProjectSource();

            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            roundTrip.GetAs2Text().ShouldBe(source.GetAs2Text());
        }
    }

    [Fact]
    public void Decompiled_aggregate_literals_round_trip_semantically_through_source_hir()
    {
        ShockwaveFlash.Avm1.Action[] actions =
        [
            new ActionPush([PushValue.String("arrayResult")]),
            new ActionPush([
                PushValue.Integer(3),
                PushValue.Integer(2),
                PushValue.Integer(1),
                PushValue.Integer(3)
            ]),
            new ActionInitArray(),
            new ActionSetVariable(),
            new ActionPush([PushValue.String("objectResult")]),
            new ActionPush([
                PushValue.String("first"),
                PushValue.Integer(1),
                PushValue.String("second"),
                PushValue.Integer(2),
                PushValue.Integer(2)
            ]),
            new ActionInitObject(),
            new ActionSetVariable()
        ];
        var source = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 7).ProjectSource();
        var options = CreateFunction2Options(registerCount: 4, temporaryCount: 4);

        var artifact = Compile(source, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var originalMachine = new Avm1Machine(swfVersion: 7);
        var compiledMachine = new Avm1Machine(swfVersion: 7);
        originalMachine.Execute(actions, strict: true);
        compiledMachine.Execute(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            strict: true);

        var originalArray = originalMachine.Globals["arrayResult"]
            .ShouldBeOfType<Avm1Array>();
        var compiledArray = compiledMachine.Globals["arrayResult"]
            .ShouldBeOfType<Avm1Array>();
        compiledArray.Items.Select(item => item.AsNumber).ShouldBe(
            originalArray.Items.Select(item => item.AsNumber));

        var originalObject = originalMachine.Globals["objectResult"]
            .ShouldBeOfType<Avm1Object>();
        var compiledObject = compiledMachine.Globals["objectResult"]
            .ShouldBeOfType<Avm1Object>();
        compiledObject["first"].AsNumber.ShouldBe(originalObject["first"].AsNumber);
        compiledObject["second"].AsNumber.ShouldBe(originalObject["second"].AsNumber);
    }

    [Fact]
    public void If_without_else_builds_explicit_cfg_and_executes_both_paths()
    {
        var builder = new Avm1SourceArena.Builder();
        var condition = AddDynamicName(builder, "condition");
        var thenStatement = AddAssignmentStatement(builder, "result", 1);
        var thenBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [thenStatement]);
        var ifOrigin = builder.AddOrigin(0, 3, 5, 21);
        var ifStatement = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: condition,
            children: [thenBlock],
            origin: ifOrigin);
        var joinedStatement = AddAssignmentStatement(builder, "joined", 9);

        var artifact = Compile(CreateMethod(builder, [ifStatement, joinedStatement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Blocks.Count.ShouldBe(3);
        artifact.Mir.BlockLayout.Select(block => block.Value).ShouldBe([0, 1, 2]);
        artifact.Mir.EntryBlock.ShouldBe(new Avm1MirBlockIndex(0));
        var branch = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.BranchIfTrue);
        branch.Operand.ShouldBe(artifact.Mir.Instructions[0].Result);
        branch.Target.ShouldBe(new Avm1MirBlockIndex(1));
        branch.AlternativeTarget.ShouldBe(new Avm1MirBlockIndex(2));
        branch.Effects.ShouldBe(Avm1MirEffect.ControlFlow);
        artifact.StackLir.ShouldNotBeNull().Blocks.Count.ShouldBe(3);
        artifact.StackLir.Instructions
            .Where(instruction => instruction.Kind is Avm1StackLirInstructionKind.Branch)
            .Select(instruction => instruction.Opcode).ShouldBe([
                ActionOpcode.If,
                ActionOpcode.Jump,
                ActionOpcode.Jump
            ]);
        artifact.ActionBody.ShouldNotBeNull().Actions.OfType<ActionIf>()
            .ShouldHaveSingleItem();
        artifact.ActionBody.Actions.OfType<ActionJump>().Count().ShouldBe(2);
        artifact.InstructionMap
            .Where(mapping => mapping.MirInstruction == branch.Index)
            .Select(mapping => mapping.Origin).ShouldBe([ifOrigin, ifOrigin]);
        DecompileBack(artifact).ShouldContain("if (");

        foreach (var conditionValue in new[] { 0, 1 })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(conditionValue);
            machine.Globals["result"] = new Avm1Number(0);
            machine.Execute(artifact.ActionBody.Actions, strict: true);

            machine.Globals["result"].AsNumber.ShouldBe(conditionValue);
            machine.Globals["joined"].AsNumber.ShouldBe(9d);
        }
    }

    [Fact]
    public void Basic_optimization_keeps_the_true_if_arm_as_fallthrough()
    {
        var builder = new Avm1SourceArena.Builder();
        var condition = AddDynamicName(builder, "condition");
        var thenBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 1)]);
        var ifOrigin = builder.AddOrigin(0, 3, 5, 21);
        var ifStatement = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: condition,
            children: [thenBlock],
            origin: ifOrigin);
        var joinedStatement = AddAssignmentStatement(builder, "joined", 9);
        var source = CreateMethod(builder, [ifStatement, joinedStatement]);

        var baseline = Compile(source);
        var optimized = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        baseline.Succeeded.ShouldBeTrue(Describe(baseline));
        optimized.Succeeded.ShouldBeTrue(Describe(optimized));
        baseline.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionJump>().Count().ShouldBe(2);
        optimized.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionJump>().ShouldBeEmpty();
        optimized.ActionBody.Actions.OfType<ActionNot>()
            .ShouldHaveSingleItem();
        optimized.ActionBody.Actions.OfType<ActionIf>()
            .ShouldHaveSingleItem();
        optimized.Bytecode.Length.ShouldBe(baseline.Bytecode.Length - 9);

        var mirBranch = optimized.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.BranchIfTrue);
        optimized.StackLir.ShouldNotBeNull().Instructions
            .Where(instruction => instruction.MirInstruction == mirBranch.Index)
            .Select(instruction => (instruction.Kind, instruction.Opcode))
            .ShouldBe([
                (Avm1StackLirInstructionKind.Action, ActionOpcode.Not),
                (Avm1StackLirInstructionKind.Branch, ActionOpcode.If)
            ]);
        optimized.InstructionMap
            .Where(mapping => mapping.MirInstruction == mirBranch.Index)
            .Select(mapping => mapping.Origin)
            .ShouldBe([ifOrigin, ifOrigin]);

        foreach (var conditionValue in new[] { 0, 1 })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(conditionValue);
            machine.Globals["result"] = new Avm1Number(0);

            machine.Execute(optimized.ActionBody.Actions, strict: true);

            machine.Globals["result"].AsNumber.ShouldBe(conditionValue);
            machine.Globals["joined"].AsNumber.ShouldBe(9d);
        }
    }

    [Fact]
    public void Basic_trace_scheduler_forms_deterministic_if_else_traces()
    {
        var builder = new Avm1SourceArena.Builder();
        var thenBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 1)]);
        var elseBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 2)]);
        var ifStatement = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "condition"),
            children: [thenBlock, elseBlock]);
        var joinedStatement = AddAssignmentStatement(builder, "joined", 3);
        var source = CreateMethod(
            builder,
            [
                ifStatement,
                joinedStatement,
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddDynamicName(builder, "joined"))
            ]);
        var options = new Avm1CompilationOptions(7)
        {
            OptimizationLevel = Avm1OptimizationLevel.Basic
        };

        var baseline = Compile(source);
        var artifact = Compile(source, options);
        var repeated = Compile(source, options);

        baseline.Succeeded.ShouldBeTrue(Describe(baseline));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        repeated.Succeeded.ShouldBeTrue(Describe(repeated));
        artifact.Mir.Blocks.Select(block => block.Index.Value)
            .ShouldBe([0, 1, 2, 3]);
        artifact.Mir.BlockLayout.Select(block => block.Value)
            .ShouldBe([0, 2, 1, 3]);
        artifact.StackLir.ShouldNotBeNull().Blocks
            .Select(block => block.MirBlock.Value)
            .ShouldBe([0, 2, 1, 3]);
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionNot>().ShouldBeEmpty();
        artifact.ActionBody.Actions.OfType<ActionIf>()
            .ShouldHaveSingleItem();
        artifact.ActionBody.Actions.OfType<ActionJump>()
            .ShouldHaveSingleItem();
        artifact.Bytecode.Length.ShouldBe(baseline.Bytecode.Length - 15);
        artifact.Bytecode.ToArray().ShouldBe(repeated.Bytecode.ToArray());

        foreach (var testCase in new[]
        {
            (Condition: 0, Result: 2),
            (Condition: 1, Result: 1)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(testCase.Condition);

            machine.Execute(artifact.ActionBody.Actions, strict: true);

            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
            machine.Globals["joined"].AsNumber.ShouldBe(3d);
            machine.ReturnValue.AsNumber.ShouldBe(3d);
        }
    }

    [Fact]
    public void Basic_trace_scheduler_preserves_cyclic_recovery_order()
    {
        var builder = new Avm1SourceArena.Builder();
        var initialize = AddAssignmentStatement(builder, "i", 0);
        var increment = AddAssignmentStatement(
            builder,
            "i",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 1)));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 3)),
            children: [increment]);
        var source = CreateMethod(
            builder,
            [
                initialize,
                loop,
                builder.AddStatement(
                    Avm1SourceStatementKind.Return,
                    expression: AddDynamicName(builder, "i"))
            ]);

        var artifact = Compile(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.BlockLayout.Select(block => block.Value)
            .ShouldBe([0, 1, 2, 3]);
        var positions = artifact.Mir.BlockLayout
            .Select((block, index) => (block, index))
            .ToDictionary(item => item.block, item => item.index);
        artifact.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.Branch &&
            positions[instruction.Target] < positions[new Avm1MirBlockIndex(2)]);

        var machine = new Avm1Machine(swfVersion: 7);
        machine.Execute(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            strict: true);
        machine.ReturnValue.AsNumber.ShouldBe(3d);
    }

    [Fact]
    public void If_else_executes_one_arm_and_rejoins()
    {
        var builder = new Avm1SourceArena.Builder();
        var thenBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 1)]);
        var elseBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 2)]);
        var ifStatement = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "condition"),
            children: [thenBlock, elseBlock]);
        var joinedStatement = AddAssignmentStatement(builder, "joined", 3);

        var artifact = Compile(CreateMethod(builder, [ifStatement, joinedStatement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Blocks.Count.ShouldBe(4);
        artifact.Mir.BlockLayout.Select(block => block.Value).ShouldBe([0, 1, 2, 3]);
        artifact.StackLir.ShouldNotBeNull().Instructions
            .Where(instruction => instruction.Kind is Avm1StackLirInstructionKind.Branch)
            .Select(instruction => instruction.Opcode).ShouldBe([
                ActionOpcode.If,
                ActionOpcode.Jump,
                ActionOpcode.Jump,
                ActionOpcode.Jump
            ]);

        foreach (var testCase in new[] { (Condition: 0, Result: 2), (Condition: 1, Result: 1) })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(testCase.Condition);
            machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
            machine.Globals["joined"].AsNumber.ShouldBe(3d);
        }
    }

    [Fact]
    public void Nested_if_with_early_returns_keeps_only_the_reachable_join()
    {
        var builder = new Avm1SourceArena.Builder();
        var returnStatement = builder.AddStatement(Avm1SourceStatementKind.Return);
        var innerThen = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children:
            [
                AddAssignmentStatement(builder, "result", 1),
                returnStatement
            ]);
        var innerElse = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children:
            [
                AddAssignmentStatement(builder, "result", 2),
                returnStatement
            ]);
        var innerIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "inner"),
            children: [innerThen, innerElse]);
        var outerThen = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [innerIf]);
        var outerIf = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "outer"),
            children: [outerThen]);
        var fallthrough = AddAssignmentStatement(builder, "result", 3);

        var artifact = Compile(CreateMethod(builder, [outerIf, fallthrough]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.BlockLayout.Select(block => block.Value).ShouldBe([
            0, 1, 3, 4, 5, 2
        ]);
        var unreachableJoin = artifact.Mir.Blocks
            .Where(block => !block.IsReachable)
            .ShouldHaveSingleItem();
        unreachableJoin.Instructions.Count.ShouldBe(0);

        foreach (var testCase in new[]
        {
            (Outer: 0, Inner: 0, Result: 3),
            (Outer: 1, Inner: 0, Result: 2),
            (Outer: 1, Inner: 1, Result: 1)
        })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["outer"] = new Avm1Number(testCase.Outer);
            machine.Globals["inner"] = new Avm1Number(testCase.Inner);
            machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
        }
    }

    [Fact]
    public void If_condition_is_evaluated_exactly_once()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children:
            [
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)
            ]);
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "count"), increment]);
        var thenBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 1)]);
        var ifStatement = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: condition,
            children: [thenBlock]);
        var artifact = Compile(CreateMethod(builder, [ifStatement]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals["count"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(1d);
        machine.Globals["result"].AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void Decompiled_if_round_trips_through_source_hir_and_executes()
    {
        var builder = new Avm1SourceArena.Builder();
        var thenBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 1)]);
        var elseBlock = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 2)]);
        var ifStatement = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "condition"),
            children: [thenBlock, elseBlock]);
        var firstArtifact = Compile(CreateMethod(builder, [ifStatement]));
        firstArtifact.Succeeded.ShouldBeTrue(Describe(firstArtifact));
        var projected = Avm1Decompiler.DecompileMethod(
            firstArtifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource();

        var secondArtifact = Compile(projected);

        secondArtifact.Succeeded.ShouldBeTrue(Describe(secondArtifact));
        projected.GetAs2Text().ShouldContain("if (");
        foreach (var testCase in new[] { (Condition: 0, Result: 2), (Condition: 1, Result: 1) })
        {
            var machine = new Avm1Machine(swfVersion: 7);
            machine.Globals["condition"] = new Avm1Number(testCase.Condition);
            machine.Execute(
                secondArtifact.ActionBody.ShouldNotBeNull().Actions,
                strict: true);
            machine.Globals["result"].AsNumber.ShouldBe(testCase.Result);
        }
    }

    [Fact]
    public void For_in_enumerates_object_keys_and_semantically_round_trips()
    {
        var builder = new Avm1SourceArena.Builder();
        var key = AddLocalSymbol(builder, "key");
        var increment = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var rememberKey = AddAssignmentStatement(
            builder,
            "lastKey",
            AddSymbolReference(builder, key));
        var loop = AddForIn(
            builder,
            key,
            "source",
            [increment, rememberKey]);
        var options = CreateFunction2Options(registerCount: 1, temporaryCount: 1);
        var firstArtifact = Compile(CreateMethod(builder, [loop]), options);

        firstArtifact.Succeeded.ShouldBeTrue(Describe(firstArtifact));
        firstArtifact.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1MirInstructionKind.BeginEnumeration);
        firstArtifact.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1MirInstructionKind.EnumerateNext);
        firstArtifact.ActionBody.ShouldNotBeNull().Actions
            .ShouldContain(action => action is ActionEnumerate2);
        firstArtifact.ActionBody.MaximumStackDepthIsExact.ShouldBeFalse();
        var projected = Avm1Decompiler.DecompileMethod(
            firstArtifact.ActionBody.Actions,
            swfVersion: 7).ProjectSource();

        projected.IsComplete.ShouldBeTrue();
        projected.GetAs2Text().ShouldContain(" in source)");
        var secondArtifact = Compile(projected, options);
        secondArtifact.Succeeded.ShouldBeTrue(Describe(secondArtifact));
        var machine = CreateEnumerationMachine("source", "first", "second", "third");
        machine.Globals["count"] = new Avm1Number(0);

        machine.Execute(
            secondArtifact.ActionBody.ShouldNotBeNull().Actions,
            strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(3d);
        new[] { "first", "second", "third" }
            .ShouldContain(machine.Globals["lastKey"].AsString);
    }

    [Fact]
    public void Sequential_for_in_loops_reuse_the_key_slot_and_cleanup_breaks()
    {
        var builder = new Avm1SourceArena.Builder();
        var key = AddLocalSymbol(builder, "key");
        var skip = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.StrictEqual,
                AddSymbolReference(builder, key),
                AddStringLiteral(builder, "skip")),
            children:
            [
                builder.AddStatement(Avm1SourceStatementKind.Continue)
            ]);
        var incrementCount = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var continueLoop = AddForIn(
            builder,
            key,
            "source",
            [skip, incrementCount]);
        var incrementBreaks = AddAssignmentStatement(
            builder,
            "breaks",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "breaks"),
                AddIntegerLiteral(builder, 1)));
        var breakAtTwo = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.StrictEqual,
                AddDynamicName(builder, "breaks"),
                AddIntegerLiteral(builder, 2)),
            children:
            [
                builder.AddStatement(Avm1SourceStatementKind.Break)
            ]);
        var breakLoop = AddForIn(
            builder,
            key,
            "source",
            [incrementBreaks, breakAtTwo]);
        var after = AddAssignmentStatement(builder, "after", 1);
        var artifact = Compile(
            CreateMethod(builder, [continueLoop, breakLoop, after]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(1);
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([1]);
        artifact.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1MirInstructionKind.EndEnumeration);
        var machine = CreateEnumerationMachine("source", "first", "skip", "third");
        machine.Globals["count"] = new Avm1Number(0);
        machine.Globals["breaks"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(2d);
        machine.Globals["breaks"].AsNumber.ShouldBe(2d);
        machine.Globals["after"].AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void Labeled_break_from_nested_for_in_cleans_both_enumeration_streams()
    {
        var builder = new Avm1SourceArena.Builder();
        var outerKey = AddLocalSymbol(builder, "outerKey");
        var innerKey = AddLocalSymbol(builder, "innerKey");
        var outerLabel = builder.AddLabel("outer");
        var increment = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var leaveOuter = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.StrictEqual,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 2)),
            children:
            [
                builder.AddStatement(
                    Avm1SourceStatementKind.Break,
                    label: outerLabel)
            ]);
        var inner = AddForIn(
            builder,
            innerKey,
            "innerSource",
            [increment, leaveOuter]);
        var outer = AddForIn(
            builder,
            outerKey,
            "outerSource",
            [inner],
            outerLabel);
        var after = AddAssignmentStatement(builder, "after", 1);
        var artifact = Compile(
            CreateMethod(builder, [outer, after]),
            CreateFunction2Options(registerCount: 2, temporaryCount: 2));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(2);
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.EndEnumeration).ShouldBe(2);
        var labeledBreak = artifact.Mir.Instructions.Single(instruction =>
            instruction.Kind is Avm1MirInstructionKind.Break);
        labeledBreak.AlternativeTarget.IsValid.ShouldBeTrue();
        var machine = CreateEnumerationMachine("outerSource", "a", "b");
        machine.Globals["innerSource"] = CreateObject("x", "y", "z");
        machine.Globals["count"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(2d);
        machine.Globals["after"].AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void Labeled_continue_from_nested_for_in_cleans_only_the_inner_stream()
    {
        var builder = new Avm1SourceArena.Builder();
        var outerKey = AddLocalSymbol(builder, "outerKey");
        var innerKey = AddLocalSymbol(builder, "innerKey");
        var outerLabel = builder.AddLabel("outer");
        var increment = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var continueOuter = builder.AddStatement(
            Avm1SourceStatementKind.Continue,
            label: outerLabel);
        var wrongInnerPath = AddAssignmentStatement(builder, "wrongInnerPath", 1);
        var inner = AddForIn(
            builder,
            innerKey,
            "innerSource",
            [increment, continueOuter, wrongInnerPath]);
        var wrongOuterPath = AddAssignmentStatement(builder, "wrongOuterPath", 1);
        var outer = AddForIn(
            builder,
            outerKey,
            "outerSource",
            [inner, wrongOuterPath],
            outerLabel);
        var after = AddAssignmentStatement(builder, "after", 1);
        var artifact = Compile(
            CreateMethod(builder, [outer, after]),
            CreateFunction2Options(registerCount: 2, temporaryCount: 2));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.Instructions.Count(instruction =>
            instruction.Kind is Avm1MirInstructionKind.EndEnumeration).ShouldBe(1);
        var machine = CreateEnumerationMachine("outerSource", "a", "b");
        machine.Globals["innerSource"] = CreateObject("x", "y");
        machine.Globals["count"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(2d);
        machine.Globals.Members.ShouldNotContainKey("wrongInnerPath");
        machine.Globals.Members.ShouldNotContainKey("wrongOuterPath");
        machine.Globals["after"].AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void Return_from_for_in_preserves_its_value_after_enumeration_cleanup()
    {
        var builder = new Avm1SourceArena.Builder();
        var key = AddLocalSymbol(builder, "key");
        var found = AddAssignmentStatement(builder, "found", 1);
        var @return = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: AddSymbolReference(builder, key));
        var loop = AddForIn(builder, key, "source", [found, @return]);
        var wrongPath = AddAssignmentStatement(builder, "wrongPath", 1);
        var artifact = Compile(
            CreateMethod(builder, [loop, wrongPath]),
            CreateFunction2Options(registerCount: 2, temporaryCount: 2));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.Mir.TemporaryCount.ShouldBe(2);
        artifact.Mir.Instructions.Select(instruction => instruction.Kind)
            .ShouldContain(Avm1MirInstructionKind.EndEnumeration);
        var machine = CreateEnumerationMachine("source", "first", "second");

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["found"].AsNumber.ShouldBe(1d);
        machine.Globals.Members.ShouldNotContainKey("wrongPath");
        new[] { "first", "second" }.ShouldContain(machine.ReturnValue.AsString);
    }

    [Fact]
    public void Optimized_for_in_return_evaluates_its_value_once_before_cleanup()
    {
        var builder = new Avm1SourceArena.Builder();
        var key = AddLocalSymbol(builder, "key");
        var increment = AddAssignmentExpression(
            builder,
            "evaluations",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "evaluations"),
                AddIntegerLiteral(builder, 1)));
        var @return = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: increment);
        var loop = AddForIn(builder, key, "source", [@return]);
        var method = CreateMethod(builder, [loop]);
        var options = CreateFunction2Options(registerCount: 2, temporaryCount: 2) with
        {
            OptimizationLevel = Avm1OptimizationLevel.Basic
        };
        var artifact = Compile(method, options);

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var cleanupBlocks = artifact.Mir.Blocks
            .Where(block => block.IsReachable &&
                block.Instructions.Count != 0 &&
                artifact.Mir.Instructions[
                    block.Instructions.Start + block.Instructions.Count - 1].Kind is
                    Avm1MirInstructionKind.EndEnumeration)
            .ToArray();
        cleanupBlocks.Length.ShouldBe(1);
        cleanupBlocks[0].Instructions.Count.ShouldBe(1);
        var machine = CreateEnumerationMachine("source", "first", "second", "third");
        machine.Globals["evaluations"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["evaluations"].AsNumber.ShouldBe(1d);
        machine.ReturnValue.AsNumber.ShouldBe(1d);
    }

    [Fact]
    public void For_in_assigns_each_key_through_a_member_lvalue()
    {
        var builder = new Avm1SourceArena.Builder();
        var memberTarget = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children:
            [
                AddDynamicName(builder, "holder"),
                AddStringLiteral(builder, "current")
            ]);
        var increment = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.ForIn,
            expression: memberTarget,
            secondaryExpression: AddDynamicName(builder, "source"),
            children: [increment]);
        var artifact = Compile(
            CreateMethod(builder, [loop]),
            CreateFunction2Options(registerCount: 1, temporaryCount: 1));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var machine = CreateEnumerationMachine("source", "first", "second");
        var holder = new Avm1Object();
        machine.Globals["holder"] = holder;
        machine.Globals["count"] = new Avm1Number(0);

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.Globals["count"].AsNumber.ShouldBe(2d);
        new[] { "first", "second" }.ShouldContain(holder["current"].AsString);
    }

    [Fact]
    public void For_in_spills_enumerator_with_an_empty_register_file()
    {
        var builder = new Avm1SourceArena.Builder();
        var key = AddLocalSymbol(builder, "key");
        var loop = AddForIn(builder, key, "source", []);

        var artifact = Compile(
            CreateMethod(builder, [loop]),
            CreateFunction2Options(registerCount: 0, temporaryCount: 0));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        AssertActivationSpills(artifact, expectedCount: 1);
        var machine = CreateEnumerationMachine("source", "first", "second");

        machine.Execute(artifact.ActionBody.ShouldNotBeNull().Actions, strict: true);

        machine.UnsupportedOpcodes.ShouldBeEmpty();
    }

    [Fact]
    public void For_in_uses_the_automatic_legacy_temporary_register()
    {
        var builder = new Avm1SourceArena.Builder();
        var key = AddLocalSymbol(builder, "key");
        var loop = AddForIn(builder, key, "source", []);

        var artifact = Compile(CreateMethod(builder, [loop]));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.StackLir.ShouldNotBeNull().TemporaryRegisters.ShouldBe([0]);
    }

    [Fact]
    public void Rejects_malformed_if_before_stack_lowering()
    {
        var builder = new Avm1SourceArena.Builder();
        var origin = builder.AddOrigin(0, 8, 4, 13);
        var branch = builder.AddStatement(Avm1SourceStatementKind.Block);
        var malformed = builder.AddStatement(
            Avm1SourceStatementKind.If,
            children: [branch],
            origin: origin);

        var artifact = Compile(CreateMethod(builder, [malformed]));

        artifact.Succeeded.ShouldBeFalse();
        artifact.StackLir.ShouldBeNull();
        artifact.ActionBody.ShouldBeNull();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CMP102" &&
            diagnostic.Origin == origin &&
            diagnostic.Message.Contains("condition", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalized_hir_gate_accepts_the_phase2_compiler_decompiler_corpus()
    {
        (string Name, Avm1SourceMethod Method)[] cases =
        [
            ("literal", CreateLiteralReturn(Avm1SourceLiteralKind.Integer, 42)),
            ("binary", CreateBinaryReturn(Avm1SourceOperator.Add)),
            ("unary", CreateUnaryReturn(Avm1SourceOperator.LogicalNot)),
            ("member assignment result", CreateMemberAssignmentReturn().Method),
            ("conditional value", CreateOuterValueConditional()),
            ("short circuit", CreateEquivalenceShortCircuitMethod()),
            ("if else", CreateEquivalenceIfMethod()),
            ("counting loop", CreateCountingWhileMethod()),
            ("assignment condition loop", CreateAssignmentConditionWhileMethod()),
            ("do while", CreateEquivalenceDoWhileMethod()),
            ("multi-clause for", CreateEquivalenceForMethod()),
            ("for in", CreateEquivalenceForInMethod()),
            ("switch", CreateEquivalenceSwitchMethod()),
            ("computed call", CreateEquivalenceCallMethod()),
            ("aggregate", CreateEquivalenceAggregateMethod()),
            ("member delete", CreateMemberDeleteReturn()),
            ("computed-member delete", CreateComputedMemberDeleteReturn()),
            ("computed-name delete", CreateComputedNameDeleteReturn())
        ];
        var failures = new List<string>();
        var options = CreateFunction2Options(registerCount: 4, temporaryCount: 4);

        foreach (var (name, method) in cases)
        {
            var artifact = Compile(method, options);
            artifact.Succeeded.ShouldBeTrue(Describe(artifact));
            var decompilation = Avm1Decompiler.DecompileMethod(
                artifact.ActionBody.ShouldNotBeNull().Actions,
                swfVersion: 7);
            var projected = decompilation.ProjectSource();
            var equivalence = Avm1SourceEquivalence.Compare(method, projected);
            if (equivalence.IsEquivalent)
                continue;

            failures.Add(
                $"{name}: {DescribeEquivalence(equivalence)}{Environment.NewLine}" +
                $"expected:{Environment.NewLine}{method.GetAs2Text()}" +
                $"actual:{Environment.NewLine}{projected.GetAs2Text()}" +
                $"actions:{Environment.NewLine}" +
                ShockwaveFlash.Avm1.Text.Avm1Disassembler.Disassemble(
                    artifact.ActionBody.Actions,
                    swfVersion: 7));
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    private static Avm1SourceMethod CreateLiteralReturn(
        Avm1SourceLiteralKind kind,
        object? value)
    {
        var builder = new Avm1SourceArena.Builder();
        var literal = kind switch
        {
            Avm1SourceLiteralKind.Boolean => builder.AddLiteral(
                kind,
                booleanValue: (bool)value!),
            Avm1SourceLiteralKind.Integer => builder.AddLiteral(
                kind,
                integerValue: (int)value!),
            Avm1SourceLiteralKind.Number => builder.AddLiteral(
                kind,
                numberValue: (double)value!),
            Avm1SourceLiteralKind.String => builder.AddLiteral(
                kind,
                stringValue: builder.InternString((string)value!)),
            _ => builder.AddLiteral(kind)
        };
        var expression = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: literal);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: expression);
        return CreateMethod(builder, [statement]);
    }

    private static Avm1SourceMethod CreateBinaryReturn(Avm1SourceOperator @operator)
    {
        var builder = new Avm1SourceArena.Builder();
        var left = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1));
        var right = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 2));
        var binary = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            @operator,
            children: [left, right]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: binary);
        return CreateMethod(builder, [statement]);
    }

    private static Avm1SourceMethod CreateUnaryReturn(Avm1SourceOperator @operator)
    {
        var builder = new Avm1SourceArena.Builder();
        var operand = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1));
        var unary = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            @operator,
            children: [operand]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: unary);
        return CreateMethod(builder, [statement]);
    }

    private static (Avm1SourceMethod Method, SourceOriginIndex AssignmentOrigin)
        CreateMemberAssignmentReturn()
    {
        var builder = new Avm1SourceArena.Builder();
        var receiver = AddDynamicName(builder, "object");
        var memberName = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("value")));
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children: [receiver, memberName]);
        var value = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1));
        var assignmentOrigin = builder.AddOrigin(0, 3, 7, 23);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            origin: assignmentOrigin,
            children: [member, value]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: assignment);
        return (CreateMethod(builder, [statement]), assignmentOrigin);
    }

    private static Avm1SourceMethod CreateMemberDeleteReturn()
    {
        var builder = new Avm1SourceArena.Builder();
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children:
            [
                AddDynamicName(builder, "object"),
                AddStringLiteral(builder, "field")
            ]);
        var delete = builder.AddExpression(
            Avm1SourceExpressionKind.Delete,
            children: [member]);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: delete)]);
    }

    private static Avm1SourceMethod CreateComputedNameDeleteReturn()
    {
        var builder = new Avm1SourceArena.Builder();
        var resolveName = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "resolveName")]);
        var computedName = builder.AddExpression(
            Avm1SourceExpressionKind.ComputedDynamicName,
            children: [resolveName]);
        var delete = builder.AddExpression(
            Avm1SourceExpressionKind.Delete,
            children: [computedName]);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: delete)]);
    }

    private static Avm1SourceMethod CreateComputedMemberDeleteReturn()
    {
        var builder = new Avm1SourceArena.Builder();
        var receiver = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "resolveObject")]);
        var key = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [AddDynamicName(builder, "resolveKey")]);
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children: [receiver, key]);
        var delete = builder.AddExpression(
            Avm1SourceExpressionKind.Delete,
            children: [member]);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: delete)]);
    }

    private static SourceStatementIndex AddSwitchCase(
        Avm1SourceArena.Builder builder,
        SourceExpressionIndex value,
        params SourceStatementIndex[] children) =>
        builder.AddStatement(
            Avm1SourceStatementKind.SwitchCase,
            expression: value,
            children: children);

    private static SourceStatementIndex AddSwitchDefault(
        Avm1SourceArena.Builder builder,
        params SourceStatementIndex[] children) =>
        builder.AddStatement(
            Avm1SourceStatementKind.SwitchDefault,
            children: children);

    private static SourceExpressionIndex AddDynamicName(
        Avm1SourceArena.Builder builder,
        string name) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.DynamicName,
            name: builder.InternString(name));

    private static Avm1SourceMethod CreateRepeatedDynamicReadMethod(
        string name,
        int occurrenceCount,
        bool returnLast)
    {
        var builder = new Avm1SourceArena.Builder();
        var statements = new List<SourceStatementIndex>(occurrenceCount);
        for (var occurrence = 0; occurrence < occurrenceCount; occurrence++)
        {
            var expression = AddDynamicName(builder, name);
            statements.Add(builder.AddStatement(
                returnLast && occurrence == occurrenceCount - 1
                    ? Avm1SourceStatementKind.Return
                    : Avm1SourceStatementKind.Expression,
                expression: expression));
        }
        return CreateMethod(builder, statements);
    }

    private static SourceExpressionIndex AddStringLiteral(
        Avm1SourceArena.Builder builder,
        string value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString(value)));

    private static SourceExpressionIndex AddIntegerLiteral(
        Avm1SourceArena.Builder builder,
        int value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: value));

    private static SourceExpressionIndex AddNumberLiteral(
        Avm1SourceArena.Builder builder,
        double value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Number,
                numberValue: value));

    private static SourceExpressionIndex AddBooleanLiteral(
        Avm1SourceArena.Builder builder,
        bool value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Boolean,
                booleanValue: value));

    private static SourceExpressionIndex AddBinaryExpression(
        Avm1SourceArena.Builder builder,
        Avm1SourceOperator @operator,
        SourceExpressionIndex left,
        SourceExpressionIndex right) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            @operator,
            children: [left, right]);

    private static SourceExpressionIndex AddSymbolReference(
        Avm1SourceArena.Builder builder,
        SourceSymbolIndex symbol) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: symbol);

    private static SourceSymbolIndex AddLocalSymbol(
        Avm1SourceArena.Builder builder,
        string name) =>
        builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            name,
            SourceTypeIndex.Invalid,
            builder.GetBuiltInType(Avm1SourceTypeKind.Unknown));

    private static SourceStatementIndex AddForIn(
        Avm1SourceArena.Builder builder,
        SourceSymbolIndex key,
        string collection,
        IReadOnlyList<SourceStatementIndex> children,
        SourceLabelIndex? label = null) =>
        builder.AddStatement(
            Avm1SourceStatementKind.ForIn,
            expression: AddSymbolReference(builder, key),
            secondaryExpression: AddDynamicName(builder, collection),
            label: label,
            flags: Avm1SourceStatementFlags.ForInDeclaresKey,
            children: children);

    private static SourceExpressionIndex AddAssignmentExpression(
        Avm1SourceArena.Builder builder,
        string name,
        int value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children:
            [
                AddDynamicName(builder, name),
                AddIntegerLiteral(builder, value)
            ]);

    private static SourceExpressionIndex AddAssignmentExpression(
        Avm1SourceArena.Builder builder,
        string name,
        SourceExpressionIndex value) =>
        builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, name), value]);

    private static SourceStatementIndex AddAssignmentStatement(
        Avm1SourceArena.Builder builder,
        string name,
        int value)
    {
        var assignment = AddAssignmentExpression(builder, name, value);
        return builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
    }

    private static SourceStatementIndex AddAssignmentStatement(
        Avm1SourceArena.Builder builder,
        string name,
        SourceExpressionIndex value)
    {
        var assignment = AddAssignmentExpression(builder, name, value);
        return builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
    }

    private static Avm1SourceMethod CreateCountingWhileMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var addToSum = AddAssignmentStatement(
            builder,
            "sum",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "sum"),
                AddDynamicName(builder, "i")));
        var increment = AddAssignmentStatement(
            builder,
            "i",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 1)));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddDynamicName(builder, "i"),
                AddIntegerLiteral(builder, 4)),
            children: [addToSum, increment]);
        return CreateMethod(builder, [loop]);
    }

    private static Avm1SourceMethod CreateAssignmentConditionWhileMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var limit = AddLocalSymbol(builder, "limit");
        var index = AddLocalSymbol(builder, "index");
        var declareLimit = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 3),
            symbol: limit);
        var declareIndex = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, -1),
            symbol: index);
        var increment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children:
            [
                AddSymbolReference(builder, index),
                AddIntegerLiteral(builder, 1)
            ]);
        var condition = AddBinaryExpression(
            builder,
            Avm1SourceOperator.Less,
            increment,
            AddSymbolReference(builder, limit));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.While,
            expression: condition,
            children:
            [
                AddAssignmentStatement(
                    builder,
                    "last",
                    AddSymbolReference(builder, index))
            ]);
        return CreateMethod(builder, [declareLimit, declareIndex, loop]);
    }

    private static Avm1SourceMethod CreateOuterValueConditional()
    {
        var builder = new Avm1SourceArena.Builder();
        var conditional = builder.AddExpression(
            Avm1SourceExpressionKind.Conditional,
            children:
            [
                AddDynamicName(builder, "condition"),
                AddIntegerLiteral(builder, 7),
                AddIntegerLiteral(builder, 9)
            ]);
        var sum = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Add,
            children: [AddIntegerLiteral(builder, 100), conditional]);
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.Assign,
            children: [AddDynamicName(builder, "result"), sum]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        return CreateMethod(builder, [statement]);
    }

    private static Avm1SourceMethod CreateEquivalenceShortCircuitMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var logical = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.LogicalAnd,
            children:
            [
                AddDynamicName(builder, "left"),
                AddDynamicName(builder, "right")
            ]);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: logical)]);
    }

    private static Avm1SourceMethod CreateEquivalenceIfMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var whenTrue = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 1)]);
        var whenFalse = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [AddAssignmentStatement(builder, "result", 2)]);
        var conditional = builder.AddStatement(
            Avm1SourceStatementKind.If,
            expression: AddDynamicName(builder, "condition"),
            children: [whenTrue, whenFalse]);
        return CreateMethod(builder, [conditional]);
    }

    private static Avm1SourceMethod CreateEquivalenceDoWhileMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var increment = AddAssignmentStatement(
            builder,
            "count",
            AddBinaryExpression(
                builder,
                Avm1SourceOperator.Add,
                AddDynamicName(builder, "count"),
                AddIntegerLiteral(builder, 1)));
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.DoWhile,
            expression: AddDynamicName(builder, "condition"),
            children: [increment]);
        return CreateMethod(builder, [loop]);
    }

    private static Avm1SourceMethod CreateEquivalenceForMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var i = AddLocalSymbol(builder, "i");
        var c = AddLocalSymbol(builder, "c");
        var iInitializer = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 0),
            symbol: i);
        var cInitializer = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: AddIntegerLiteral(builder, 10),
            symbol: c);
        var incrementI = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddSymbolReference(builder, i)]);
        var incrementC = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [AddSymbolReference(builder, c)]);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            expression: AddBinaryExpression(
                builder,
                Avm1SourceOperator.Less,
                AddSymbolReference(builder, i),
                AddIntegerLiteral(builder, 3)),
            children:
            [
                AddAssignmentStatement(
                    builder,
                    "sum",
                    AddBinaryExpression(
                        builder,
                        Avm1SourceOperator.Add,
                        AddDynamicName(builder, "sum"),
                        AddSymbolReference(builder, c)))
            ],
            initializers: [iInitializer, cInitializer],
            expressions: [incrementI, incrementC]);
        return CreateMethod(builder, [loop]);
    }

    private static Avm1SourceMethod CreateEquivalenceForInMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var key = AddLocalSymbol(builder, "key");
        var loop = AddForIn(
            builder,
            key,
            "source",
            [
                AddAssignmentStatement(
                    builder,
                    "lastKey",
                    AddSymbolReference(builder, key))
            ]);
        return CreateMethod(builder, [loop]);
    }

    private static Avm1SourceMethod CreateEquivalenceSwitchMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var caseOne = AddSwitchCase(
            builder,
            AddIntegerLiteral(builder, 1),
            AddAssignmentStatement(builder, "result", 10),
            builder.AddStatement(Avm1SourceStatementKind.Break));
        var caseTwo = AddSwitchCase(
            builder,
            AddIntegerLiteral(builder, 2),
            AddAssignmentStatement(builder, "result", 20),
            builder.AddStatement(Avm1SourceStatementKind.Break));
        var @default = AddSwitchDefault(
            builder,
            AddAssignmentStatement(builder, "result", 30));
        var @switch = builder.AddStatement(
            Avm1SourceStatementKind.Switch,
            expression: AddDynamicName(builder, "kind"),
            children: [caseOne, caseTwo, @default]);
        return CreateMethod(builder, [@switch]);
    }

    private static Avm1SourceMethod CreateEquivalenceCallMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            flags: Avm1SourceExpressionFlags.ComputedMember,
            children:
            [
                AddDynamicName(builder, "receiver"),
                AddDynamicName(builder, "key")
            ]);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children:
            [
                member,
                AddDynamicName(builder, "first"),
                AddDynamicName(builder, "second")
            ]);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: call)]);
    }

    private static Avm1SourceMethod CreateEquivalenceAggregateMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var literal = builder.AddExpression(
            Avm1SourceExpressionKind.ObjectLiteral,
            children:
            [
                AddStringLiteral(builder, "first"),
                AddDynamicName(builder, "firstValue"),
                AddStringLiteral(builder, "second"),
                AddDynamicName(builder, "secondValue")
            ]);
        return CreateMethod(
            builder,
            [builder.AddStatement(
                Avm1SourceStatementKind.Return,
                expression: literal)]);
    }

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

    private static Avm1MethodArtifact Compile(
        Avm1SourceMethod method,
        byte swfVersion = 7) =>
        new Avm1Compiler().CompileMethod(
            method,
            new Avm1CompilationOptions(swfVersion));

    private static Avm1MethodArtifact Compile(
        Avm1SourceMethod method,
        Avm1CompilationOptions options) =>
        new Avm1Compiler().CompileMethod(method, options);

    private static Avm1CompilationOptions CreateFunction2Options(
        byte registerCount,
        byte temporaryCount) =>
        new(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = registerCount == 0
                ? (byte)0
                : checked((byte)(registerCount + 1)),
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(
                1,
                temporaryCount)
        };

    private static string[] AssertActivationSpills(
        Avm1MethodArtifact artifact,
        int expectedCount)
    {
        var stackLir = artifact.StackLir.ShouldNotBeNull();
        var spills = stackLir.TemporaryStorages
            .Where(storage =>
                storage.Kind is
                    Avm1StackLirTemporaryStorageKind.ActivationName)
            .ToArray();
        spills.Length.ShouldBe(expectedCount);
        var names = spills.Select(storage =>
        {
            storage.Name.IsValid.ShouldBeTrue();
            return stackLir[storage.Name];
        }).ToArray();
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        names.ShouldAllBe(name =>
            name.StartsWith("__avm1_spill", StringComparison.Ordinal));
        return names;
    }

    private static Avm1Machine CreateEnumerationMachine(
        string globalName,
        params string[] keys)
    {
        var machine = new Avm1Machine(swfVersion: 7);
        machine.Globals[globalName] = CreateObject(keys);
        return machine;
    }

    private static Avm1Object CreateObject(params string[] keys)
    {
        var result = new Avm1Object();
        for (var i = 0; i < keys.Length; i++)
            result.Members[keys[i]] = new Avm1Number(i + 1);
        return result;
    }

    private static string DecompileBack(Avm1MethodArtifact artifact) =>
        Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.ShouldNotBeNull().Actions,
            swfVersion: 7).ProjectSource().GetAs2Text();

    private static string GetPushedString(ShockwaveFlash.Avm1.Action action) =>
        action.ShouldBeOfType<ActionPush>().PushValues.ShouldHaveSingleItem()
            .ShouldBeOfType<PushValue.PushValueString>().Value;

    private static PushValue GetPushedValue(ShockwaveFlash.Avm1.Action action) =>
        action.ShouldBeOfType<ActionPush>().PushValues.ShouldHaveSingleItem();

    private static string Describe(Avm1MethodArtifact artifact) =>
        string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} at {diagnostic.ByteOffset}: {diagnostic.Message}"));

    private static string DescribeEquivalence(Avm1SourceEquivalenceResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}"));
}
