using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Ir;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf2;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1EffectRecoveryTests
{
    [Fact]
    public void Preserves_a_discarded_dynamic_member_read()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionPop()
        ], swfVersion: 6);

        var memberRead = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.GetMember);
        var dead = Avm1DeadCodeElimination.Run(
            result.Instructions,
            result.TacIr,
            result.RegisterSsa,
            result.ValueAnalysis);

        dead.ShouldNotContain(memberRead.Index);
        result.GetStructuredAs2Text().ShouldBe(
            $"obj.value;{Environment.NewLine}");
        result.ProjectSource().GetAs2Text().ShouldBe(
            $"obj.value;{Environment.NewLine}");
    }

    [Fact]
    public void Preserves_a_discarded_dynamic_coercion()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("value")]),
            new ActionGetVariable(),
            new ActionToString(),
            new ActionPop()
        ], swfVersion: 6);

        var coercion = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.Unary &&
            instruction.Opcode is ActionOpcode.ToString);
        var dead = Avm1DeadCodeElimination.Run(
            result.Instructions,
            result.TacIr,
            result.RegisterSsa,
            result.ValueAnalysis);

        dead.ShouldNotContain(coercion.Index);
        result.GetStructuredAs2Text().ShouldBe(
            $"String(value);{Environment.NewLine}");
    }

    [Fact]
    public void Removes_a_discarded_primitive_coercion()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Integer(42)]),
            new ActionToString(),
            new ActionPop()
        ], swfVersion: 6);

        var coercion = result.TacIr.Instructions.Single(instruction =>
            instruction.Op is Avm1TacOp.Unary);
        var dead = Avm1DeadCodeElimination.Run(
            result.Instructions,
            result.TacIr,
            result.RegisterSsa,
            result.ValueAnalysis);

        dead.ShouldContain(coercion.Index);
        result.GetStructuredAs2Text().ShouldBeEmpty();
    }

    [Fact]
    public void Preserves_a_dead_register_self_update_as_source_assignment()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([
                PushValue.Register(1),
                PushValue.Integer(10)
            ]),
            new ActionAdd2(),
            new ActionStoreRegister(1),
            new ActionPop()
        ], swfVersion: 7, ctx: new FunctionContext(
            0,
            [new FunctionParameter(1, "value")]));

        var stores = result.TacIr.Instructions
            .Where(instruction => instruction.Op is Avm1TacOp.StoreRegister)
            .ToArray();
        var dead = Avm1DeadCodeElimination.Run(
            result.Instructions,
            result.TacIr,
            result.RegisterSsa,
            result.ValueAnalysis);

        stores.Length.ShouldBe(1);
        dead.ShouldNotContain(stores[0].Index);
        result.GetStructuredAs2Text().ShouldBe(
            $"value += 10;{Environment.NewLine}");
    }

    [Fact]
    public void Round_trips_no_argument_timeline_controls_as_source_intrinsics()
    {
        Avm1Action[] actions =
        [
            new ActionNextFrame(),
            new ActionPreviousFrame(),
            new ActionPlay(),
            new ActionStop(),
            new ActionStopSounds(),
            new ActionToggleQuality()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions
            .Count(instruction => instruction.Op is
                Avm1StackIrOp.TimelineControl).ShouldBe(actions.Length);
        result.TacIr.Instructions
            .Count(instruction => instruction.Op is
                Avm1TacOp.TimelineControl).ShouldBe(actions.Length);
        result.StructuredAst.Nodes
            .Count(node => node.Kind is Avm1AstNodeKind.Intrinsic)
            .ShouldBe(actions.Length);
        source.IsComplete.ShouldBeTrue();
        source.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code == "AVM1SRC004");
        source.Arena.OpaqueRegions.ShouldBeEmpty();
        source.GetAs2Text().ShouldBe(
            $"nextFrame();{Environment.NewLine}" +
            $"prevFrame();{Environment.NewLine}" +
            $"play();{Environment.NewLine}" +
            $"stop();{Environment.NewLine}" +
            $"stopAllSounds();{Environment.NewLine}" +
            $"toggleHighQuality();{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .ShouldBe(actions.Select(action => action.Opcode));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Round_trips_legacy_timeline_call_as_source_intrinsic()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("frame")]),
            new ActionGetVariable(),
            new ActionCall()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 4);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.TimelineCall &&
            instruction.Operand0.IsValid);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.TimelineCall &&
            instruction.Operand0.IsValid);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe($"call(frame);{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(4));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.Mir.Instructions.ShouldContain(instruction =>
            instruction.Kind == Avm1MirInstructionKind.TimelineCall &&
            instruction.Effects.HasFlag(Avm1MirEffect.MayInvokeUserCode));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 4).ToArray());

        var unsupported = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(3));
        unsupported.Succeeded.ShouldBeFalse();
        unsupported.Bytecode.IsEmpty.ShouldBeTrue();
        unsupported.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains(
                "Call requires SWF 4",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Round_trips_legacy_unary_intrinsics_without_unknown_actions()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Double(3.5)]),
            new ActionToInteger(),
            new ActionTrace(),
            new ActionPush([PushValue.Integer(10)]),
            new ActionRandomNumber(),
            new ActionTrace(),
            new ActionPush([PushValue.String("abc")]),
            new ActionStringLength(),
            new ActionTrace(),
            new ActionPush([PushValue.String("abc")]),
            new ActionMBStringLength(),
            new ActionTrace(),
            new ActionPush([PushValue.String("A")]),
            new ActionCharToAscii(),
            new ActionTrace(),
            new ActionPush([PushValue.Integer(65)]),
            new ActionAsciiToChar(),
            new ActionTrace(),
            new ActionPush([PushValue.String("A")]),
            new ActionMBCharToAscii(),
            new ActionTrace(),
            new ActionPush([PushValue.Integer(65)]),
            new ActionMBAsciiToChar(),
            new ActionTrace()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 5);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldNotContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        result.TacIr.Instructions.Count(instruction =>
            instruction.Op == Avm1TacOp.Unary).ShouldBe(8);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"trace(int(3.5));{Environment.NewLine}" +
            $"trace(random(10));{Environment.NewLine}" +
            $"trace(length(\"abc\"));{Environment.NewLine}" +
            $"trace(mblength(\"abc\"));{Environment.NewLine}" +
            $"trace(ord(\"A\"));{Environment.NewLine}" +
            $"trace(chr(65));{Environment.NewLine}" +
            $"trace(mbord(\"A\"));{Environment.NewLine}" +
            $"trace(mbchr(65));{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(5));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 5).ToArray());
    }

    [Fact]
    public void Round_trips_legacy_string_extract_intrinsics_without_unknown_actions()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("hello")]),
            new ActionPush([PushValue.Integer(2)]),
            new ActionPush([PushValue.Integer(3)]),
            new ActionStringExtract(),
            new ActionTrace(),
            new ActionPush([PushValue.String("world")]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Integer(4)]),
            new ActionMBStringExtract(),
            new ActionTrace()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 5);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldNotContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        result.TacIr.Instructions.Count(instruction =>
            instruction.Op == Avm1TacOp.Intrinsic).ShouldBe(2);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"trace(substring(\"hello\", 2, 3));{Environment.NewLine}" +
            $"trace(mbsubstring(\"world\", 1, 4));{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(5));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 5).ToArray());
    }

    [Fact]
    public void Round_trips_fixed_stack_host_intrinsics_without_unknown_actions()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("clip")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionGetProperty(),
            new ActionTrace(),
            new ActionPush([PushValue.String("clip")]),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.Integer(100)]),
            new ActionSetProperty(),
            new ActionPush([PushValue.String("clip")]),
            new ActionPush([PushValue.String("copy")]),
            new ActionPush([PushValue.Integer(5)]),
            new ActionCloneSprite(),
            new ActionPush([PushValue.String("copy")]),
            new ActionRemoveSprite(),
            new ActionEndDrag()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 5);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldNotContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        result.StackIr.Instructions.Count(instruction =>
            instruction.Op == Avm1StackIrOp.HostIntrinsic).ShouldBe(5);
        result.TacIr.Instructions.Count(instruction =>
            instruction.Op == Avm1TacOp.HostIntrinsic).ShouldBe(5);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"trace(getProperty(\"clip\", 0));{Environment.NewLine}" +
            $"setProperty(\"clip\", 0, 100);{Environment.NewLine}" +
            $"duplicateMovieClip(\"clip\", \"copy\", 5);{Environment.NewLine}" +
            $"removeMovieClip(\"copy\");{Environment.NewLine}" +
            $"stopDrag();{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(5));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 5).ToArray());
    }

    [Fact]
    public void Recovers_both_static_start_drag_stack_shapes()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Boolean(false)]),
            new ActionPush([PushValue.Boolean(false)]),
            new ActionPush([PushValue.String("free")]),
            new ActionStartDrag(),
            new ActionPush([PushValue.Integer(0)]),
            new ActionPush([PushValue.Integer(1)]),
            new ActionPush([PushValue.Integer(100)]),
            new ActionPush([PushValue.Integer(101)]),
            new ActionPush([PushValue.Boolean(true)]),
            new ActionPush([PushValue.Boolean(false)]),
            new ActionPush([PushValue.String("bounded")]),
            new ActionStartDrag()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 5);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldNotContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        result.StackIr.Instructions.Count(instruction =>
            instruction.Op == Avm1StackIrOp.VariadicHostIntrinsic).ShouldBe(2);
        result.TacIr.Instructions.Count(instruction =>
            instruction.Op == Avm1TacOp.VariadicHostIntrinsic).ShouldBe(2);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"startDrag(\"free\", false);{Environment.NewLine}" +
            $"startDrag(\"bounded\", false, 0, 1, 100, 101);" +
            Environment.NewLine);
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(5));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action is ActionStartDrag).ShouldBe(2);
        artifact.ActionBody.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Preserves_dynamic_start_drag_as_explicit_opaque_fallback()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Register(1)]),
            new ActionPush([PushValue.Boolean(false)]),
            new ActionPush([PushValue.String("clip")]),
            new ActionStartDrag()
        ], swfVersion: 5);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction &&
            result.Instructions[instruction.Action].Action is ActionStartDrag);
        result.StackIr.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains(
                "constraint flag is dynamic",
                StringComparison.Ordinal));
        source.IsComplete.ShouldBeFalse();
        source.Arena.OpaqueRegions.ShouldNotBeEmpty();
    }

    [Fact]
    public void Round_trips_stack_based_timeline_gotos_as_source_intrinsics()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("frame")]),
            new ActionGetVariable(),
            new ActionGotoFrame2(play: true, hasSceneBias: false, sceneBias: 0),
            new ActionPush([PushValue.String("label")]),
            new ActionGetVariable(),
            new ActionGotoFrame2(play: false, hasSceneBias: false, sceneBias: 0)
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.Count(instruction => instruction.Op is
            Avm1StackIrOp.TimelineGoto).ShouldBe(2);
        result.TacIr.Instructions
            .Where(instruction => instruction.Op is Avm1TacOp.TimelineGoto)
            .Select(instruction => instruction.IntOperand)
            .ShouldBe([1, 0]);
        result.StructuredAst.Nodes
            .Where(node => node.Kind is Avm1AstNodeKind.Intrinsic &&
                node.StartAction.Value == (int)ActionOpcode.GotoFrame2)
            .Select(node => node.IntOperand)
            .ShouldBe([1, 0]);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"gotoAndPlay(frame);{Environment.NewLine}" +
            $"gotoAndStop(label);{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionGotoFrame2>()
            .Select(action => action.Play)
            .ShouldBe([true, false]);
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Round_trips_adobe_immediate_timeline_gotos_and_folds_play()
    {
        Avm1Action[] actions =
        [
            new ActionGotoFrame(11),
            new ActionPlay(),
            new ActionGotoFrame(12),
            new ActionGoToLabel("labelPlay"),
            new ActionPlay(),
            new ActionGoToLabel("labelStop")
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.Count(instruction => instruction.Op is
            Avm1StackIrOp.TimelineGoto).ShouldBe(4);
        result.StackIr.Instructions.Count(instruction => instruction.Op is
            Avm1StackIrOp.NoOp).ShouldBe(2);
        result.TacIr.Instructions
            .Where(instruction => instruction.Op is Avm1TacOp.TimelineGoto)
            .Select(instruction => instruction.IntOperand)
            .ShouldBe([1, 0, 1, 0]);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"gotoAndPlay(12);{Environment.NewLine}" +
            $"gotoAndStop(13);{Environment.NewLine}" +
            $"gotoAndPlay(\"labelPlay\");{Environment.NewLine}" +
            $"gotoAndStop(\"labelStop\");{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .ShouldBe(actions.Select(action => action.Opcode));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Keeps_a_branch_targeted_play_separate_from_an_immediate_goto()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Boolean(true)]),
            new ActionIf(branchOffset: 5),
            new ActionGotoFrame(1),
            new ActionPlay()
        ], swfVersion: 7);

        result.TacIr.Instructions.Single(instruction =>
            instruction.Opcode is ActionOpcode.GotoFrame)
            .IntOperand.ShouldBe(0);
        result.StackIr.Instructions.Single(instruction =>
            instruction.Action.Value == 3)
            .Op.ShouldBe(Avm1StackIrOp.TimelineControl);
    }

    [Fact]
    public void Keeps_scene_biased_timeline_goto_opaque_without_layout_metadata()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Integer(4)]),
            new ActionGotoFrame2(play: true, hasSceneBias: true, sceneBias: 2)
        ], swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.TimelineGoto);
        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC002" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error);
        source.Arena.OpaqueRegions.ShouldNotBeEmpty();
        source.GetAs2Text().ShouldNotContain("gotoAndPlay");
        source.GetAs2Text().ShouldNotContain("gotoAndStop");
    }

    [Fact]
    public void Round_trips_scene_biased_timeline_gotos_with_layout_metadata()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.Integer(4)]),
            new ActionGotoFrame2(play: true, hasSceneBias: true, sceneBias: 10),
            new ActionPush([PushValue.String("intro")]),
            new ActionGotoFrame2(play: false, hasSceneBias: true, sceneBias: 0)
        ];
        var layout = new Avm1TimelineLayout(
            frameCount: 20,
            scenes:
            [
                new Avm1TimelineScene("Scene 1", 0),
                new Avm1TimelineScene("Scene 2", 10)
            ]);
        var result = Avm1Decompiler.DecompileMethod(
            actions,
            swfVersion: 7,
            options: new Avm1MethodDecompilationOptions
            {
                TimelineLayout = layout
            });
        var source = result.ProjectSource();

        result.StackIr.Instructions.Count(instruction =>
            instruction.Op == Avm1StackIrOp.TimelineGoto).ShouldBe(2);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"gotoAndPlay(\"Scene 2\", 4);{Environment.NewLine}" +
            $"gotoAndStop(\"Scene 1\", \"intro\");{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7)
            {
                TimelineLayout = layout,
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Round_trips_immediate_if_frame_loaded_regions()
    {
        Avm1Action[] actions =
        [
            new ActionWaitForFrame(frame: 11, skipCount: 2),
            new ActionPush([PushValue.String("ready")]),
            new ActionTrace()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.FrameLoaded);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.FrameLoaded &&
            instruction.IntOperand == 11);
        result.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.IfFrameLoaded);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"ifFrameLoaded(12){Environment.NewLine}" +
            $"{{{Environment.NewLine}" +
            $"    trace(\"ready\");{Environment.NewLine}" +
            $"}}{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });
        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Round_trips_stack_if_frame_loaded_regions()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("frame")]),
            new ActionGetVariable(),
            new ActionWaitForFrame2(skipCount: 2),
            new ActionPush([PushValue.String("ready")]),
            new ActionTrace()
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.FrameLoaded &&
            instruction.Operand0.IsValid);
        result.TacIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1TacOp.FrameLoaded &&
            instruction.Operand0.IsValid);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"ifFrameLoaded(frame){Environment.NewLine}" +
            $"{{{Environment.NewLine}" +
            $"    trace(\"ready\");{Environment.NewLine}" +
            $"}}{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });
        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Round_trips_immediate_url_and_fscommand_actions()
    {
        Avm1Action[] actions =
        [
            new ActionGetURL("https://example.test", "_blank"),
            new ActionGetURL("FSCommand:quit", string.Empty),
            new ActionGetURL("FSCommand:fullscreen", "true")
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.Count(instruction =>
            instruction.Op is Avm1StackIrOp.GetUrl).ShouldBe(3);
        result.TacIr.Instructions.Count(instruction =>
            instruction.Op is Avm1TacOp.GetUrl).ShouldBe(3);
        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"getURL(\"https://example.test\", \"_blank\");{Environment.NewLine}" +
            $"fscommand(\"quit\");{Environment.NewLine}" +
            $"fscommand(\"fullscreen\", \"true\");{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .ShouldAllBe(action => action is ActionGetURL);
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Round_trips_stack_url_request_with_http_method()
    {
        Avm1Action[] actions =
        [
            new ActionPush([PushValue.String("https://example.test")]),
            new ActionPush([PushValue.String("_self")]),
            new ActionGetURL2(GetUrlFlags.MethodGet)
        ];
        var result = Avm1Decompiler.DecompileMethod(actions, swfVersion: 7);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"getURL(\"https://example.test\", \"_self\", \"GET\");" +
            Environment.NewLine);
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var request = artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionGetURL2>()
            .ShouldHaveSingleItem();
        request.Flags.ShouldBe(GetUrlFlags.MethodGet);
        artifact.Bytecode.ToArray().ShouldBe(
            Avm1Action.EncodeCollection(actions, swfVersion: 7).ToArray());
    }

    [Fact]
    public void Recovers_and_compiles_the_url_loading_family()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionGetURL("level.swf", "_level2"),
            new ActionPush([PushValue.String("movie.swf")]),
            new ActionPush([PushValue.String("holder")]),
            new ActionGetURL2(GetUrlFlags.LoadTarget),
            new ActionPush([PushValue.String("variables.txt")]),
            new ActionPush([PushValue.String("holder")]),
            new ActionGetURL2(
                GetUrlFlags.MethodGet |
                GetUrlFlags.LoadTarget |
                GetUrlFlags.LoadVariables),
            new ActionPush([PushValue.String("level-variables.txt")]),
            new ActionPush([PushValue.String("_level3")]),
            new ActionGetURL2(
                GetUrlFlags.MethodPost |
                GetUrlFlags.LoadVariables)
        ], swfVersion: 7);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"loadMovieNum(\"level.swf\", 2);{Environment.NewLine}" +
            $"loadMovie(\"movie.swf\", \"holder\");{Environment.NewLine}" +
            $"loadVariables(\"variables.txt\", \"holder\", \"GET\");" +
            Environment.NewLine +
            $"loadVariablesNum(\"level-variables.txt\", 3, \"POST\");" +
            Environment.NewLine);
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var immediate = artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionGetURL>()
            .ShouldHaveSingleItem();
        immediate.Url.ShouldBe("level.swf");
        immediate.Target.ShouldBe("_level2");
        artifact.ActionBody.Actions.OfType<ActionGetURL2>()
            .Select(action => action.Flags)
            .ShouldBe([
                GetUrlFlags.LoadTarget,
                GetUrlFlags.MethodGet |
                    GetUrlFlags.LoadTarget |
                    GetUrlFlags.LoadVariables,
                GetUrlFlags.MethodPost | GetUrlFlags.LoadVariables
            ]);
    }

    [Fact]
    public void Recovers_dynamic_level_targets_without_exposing_level_strings()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("movie.swf")]),
            new ActionPush([PushValue.String("_level")]),
            new ActionPush([PushValue.String("level")]),
            new ActionGetVariable(),
            new ActionStringAdd(),
            new ActionGetURL2(GetUrlFlags.MethodNone),
            new ActionPush([PushValue.String("variables.txt")]),
            new ActionPush([PushValue.String("_level")]),
            new ActionPush([PushValue.String("level")]),
            new ActionGetVariable(),
            new ActionStringAdd(),
            new ActionGetURL2(
                GetUrlFlags.MethodGet | GetUrlFlags.LoadVariables)
        ], swfVersion: 7);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"loadMovieNum(\"movie.swf\", level);{Environment.NewLine}" +
            $"loadVariablesNum(\"variables.txt\", level, \"GET\");" +
            Environment.NewLine);
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());

        var artifact = new Avm1Compiler().CompileMethod(
            source,
            new Avm1CompilationOptions(7));
        artifact.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Count(action => action is ActionStringAdd)
            .ShouldBe(2);
        artifact.ActionBody.Actions.OfType<ActionGetURL2>()
            .Select(action => action.Flags)
            .ShouldBe([
                GetUrlFlags.MethodNone,
                GetUrlFlags.MethodGet | GetUrlFlags.LoadVariables
            ]);
    }

    [Fact]
    public void Keeps_invalid_url_request_flags_opaque()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("movie.swf")]),
            new ActionPush([PushValue.String("holder")]),
            new ActionGetURL2((GetUrlFlags)3)
        ], swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC004" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error);
        source.GetAs2Text().ShouldNotContain("loadMovie(");
    }

    [Fact]
    public void Keeps_non_level_load_variables_request_incomplete()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("variables.txt")]),
            new ActionPush([PushValue.String("holder")]),
            new ActionGetURL2(GetUrlFlags.LoadVariables)
        ], swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.GetUrl);
        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC002" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error);
        source.GetAs2Text().ShouldNotContain("loadVariablesNum(");
        result.GetStructuredAs2Text().ShouldNotContain("loadVariablesNum(");
    }

    [Fact]
    public void Recovers_immediate_target_scope_as_tell_target()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionSetTarget("menu"),
            new ActionPush([PushValue.String("ready")]),
            new ActionTrace(),
            new ActionSetTarget(string.Empty)
        ], swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.Count(instruction =>
            instruction.Op is Avm1StackIrOp.TargetControl).ShouldBe(2);
        result.TacIr.Instructions.Count(instruction =>
            instruction.Op is Avm1TacOp.TargetControl).ShouldBe(2);
        result.StructuredAst.Nodes.Count(node =>
            node.Kind is Avm1AstNodeKind.TargetControl).ShouldBe(2);
        source.IsComplete.ShouldBeTrue();
        source.Arena.Statements.ShouldContain(statement =>
            statement.Kind == Avm1SourceStatementKind.TellTarget);
        source.GetAs2Text().ShouldBe(
            $"tellTarget(\"menu\"){Environment.NewLine}" +
            $"{{{Environment.NewLine}" +
            $"    trace(\"ready\");{Environment.NewLine}" +
            $"}}{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());
        result.GetStructuredAstDebugText().ShouldContain(
            "target-control SetTarget");
    }

    [Fact]
    public void Recovers_stack_target_scope_as_tell_target()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("target")]),
            new ActionGetVariable(),
            new ActionSetTarget2(),
            new ActionPush([PushValue.String("ready")]),
            new ActionTrace(),
            new ActionPush([PushValue.String(string.Empty)]),
            new ActionSetTarget2()
        ], swfVersion: 7);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(
            $"tellTarget(target){Environment.NewLine}" +
            $"{{{Environment.NewLine}" +
            $"    trace(\"ready\");{Environment.NewLine}" +
            $"}}{Environment.NewLine}");
        result.GetStructuredAs2Text().ShouldBe(source.GetAs2Text());
        result.GetStructuredAstDebugText().ShouldContain(
            "target-control SetTarget2");
    }

    [Fact]
    public void Preserves_unpaired_target_transition_as_opaque_source()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionSetTarget("menu"),
            new ActionPush([PushValue.String("ready")]),
            new ActionTrace()
        ], swfVersion: 7);
        var source = result.ProjectSource();

        result.StackIr.Instructions.ShouldContain(instruction =>
            instruction.Op == Avm1StackIrOp.TargetControl);
        result.StackIr.Instructions.ShouldNotContain(instruction =>
            instruction.Op == Avm1StackIrOp.UnknownAction);
        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC005" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("SetTarget", StringComparison.Ordinal));
        var opaque = source.Arena.OpaqueRegions.ShouldHaveSingleItem();
        opaque.Kind.ShouldBe(Avm1SourceOpaqueKind.RecoveryStatement);
        source.Arena.GetBytecode(opaque).ToArray().ShouldBe(
            Avm1Action.EncodeCollection(
                [new ActionSetTarget("menu")],
                swfVersion: 7).ToArray());
        result.GetStructuredAs2Text().ShouldContain(
            "unsupported Source HIR statement: unstructured AVM1 target-scope transition");
    }

    [Fact]
    public void Projects_an_unsupported_action_as_an_opaque_payload()
    {
        Avm1Action action = new ActionUnknown(
            (ActionOpcode)0xAA,
            new byte[] { 0x12, 0x34 });
        var encoded = Avm1Action.EncodeCollection([action], swfVersion: 6);
        var result = Avm1Decompiler.DecompileMethod([action], swfVersion: 6);
        var source = result.ProjectSource();

        result.StructuredAst.Nodes.ShouldContain(node =>
            node.Kind == Avm1AstNodeKind.Opaque);
        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC004" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error);

        var opaque = source.Arena.OpaqueRegions.ShouldHaveSingleItem();
        opaque.Kind.ShouldBe(Avm1SourceOpaqueKind.UnsupportedAction);
        var statement = source.Arena.Statements.Single(candidate =>
            candidate.Kind is Avm1SourceStatementKind.Opaque);
        statement.Opaque.ShouldBe(opaque.Index);
        source.Arena.GetBytecode(opaque).ToArray().ShouldBe(encoded.ToArray());
        var origin = source.Arena[opaque.Origin];
        origin.StartOffset.ShouldBe(0);
        origin.EndOffset.ShouldBe(encoded.Length);
        source.GetAs2Text().ShouldContain(
            "unsupported Source HIR statement: 170");
    }

    [Fact]
    public void Stack_ir_dispatch_covers_every_declared_avm1_action()
    {
        var actionTypes = typeof(Avm1Action).Assembly.GetTypes()
            .Where(type =>
                type.IsPublic &&
                !type.IsAbstract &&
                type != typeof(ActionUnknown) &&
                type != typeof(ActionMalformed) &&
                type != typeof(ActionTrailingData) &&
                typeof(Avm1Action).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        var actions = actionTypes.Select(CreateDefaultAction).ToArray();

        actionTypes.Length.ShouldBe(100);
        actions.Select(action => action.Opcode)
            .OrderBy(opcode => (byte)opcode)
            .ShouldBe(Enum.GetValues<ActionOpcode>()
                .OrderBy(opcode => (byte)opcode));

        foreach (var action in actions)
        {
            var result = Avm1Decompiler.DecompileMethod([
                new ActionPush(Enumerable.Repeat(PushValue.Integer(0), 8).ToArray()),
                action
            ], swfVersion: 7);

            result.StackIr.Instructions.ShouldContain(instruction =>
                instruction.Action.Value == 1 &&
                instruction.Op != Avm1StackIrOp.UnknownAction,
                $"{action.GetType().Name} ({action.Opcode}) has no typed Stack IR dispatch.");
            result.ProjectSource().Arena.OpaqueRegions.ShouldNotContain(opaque =>
                opaque.Kind == Avm1SourceOpaqueKind.UnsupportedAction,
                $"{action.GetType().Name} ({action.Opcode}) reaches Source HIR as unsupported.");
        }
    }

    [Fact]
    public void Folds_modulo_by_zero_with_avm1_number_semantics()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([
                PushValue.Integer(1),
                PushValue.Integer(0)
            ]),
            new ActionModulo()
        ], swfVersion: 7);
        var modulo = result.TacIr.Instructions.Single(instruction =>
            instruction.Action.Value == 1);
        var fact = result.ValueAnalysis[modulo.Result];

        fact.ConstantKind.ShouldBe(Avm1ConstantKind.Number);
        double.IsNaN(fact.NumberValue).ShouldBeTrue();
    }

    [Fact]
    public void Marks_stack_ir_underflow_as_incomplete_source()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("result")]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeFalse();
        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC005" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("Stack underflow", StringComparison.Ordinal));
    }

    private static Avm1Action CreateDefaultAction(Type actionType)
    {
        if (actionType == typeof(ActionPush))
            return new ActionPush([PushValue.Undefined()]);

        var constructor = actionType.GetConstructors().ShouldHaveSingleItem();
        var arguments = constructor.GetParameters()
            .Select(parameter => CreateDefaultValue(parameter.ParameterType))
            .ToArray();
        return (Avm1Action)constructor.Invoke(arguments);
    }

    private static object? CreateDefaultValue(Type type)
    {
        if (type == typeof(string))
            return string.Empty;
        if (type.IsArray)
            return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        }
        if (type.IsValueType)
            return Activator.CreateInstance(type);

        throw new InvalidOperationException(
            $"No default action-constructor value is defined for {type}.");
    }

    [Fact]
    public void Ignores_unreachable_terminal_epilogue_cleanup_pop()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPop()
        ], swfVersion: 6);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        result.StackIr.Diagnostics.ShouldBeEmpty();
        source.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code == "AVM1SRC005" &&
            diagnostic.Message.Contains("Stack underflow", StringComparison.Ordinal));
    }

    [Fact]
    public void Does_not_emit_an_unreachable_dynamic_read_after_return()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionGetMember(),
            new ActionPop()
        ], swfVersion: 6);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe("return;" + Environment.NewLine);
        result.Reachability.ReachableBlockCount.ShouldBe(1);
    }

    [Fact]
    public void Removes_unreachable_try_tail_without_removing_return_finally_path()
    {
        var tryBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.Undefined()]),
            new ActionReturn(),
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("field")]),
            new ActionGetMember(),
            new ActionPop()
        ], swfVersion: 6);
        var finallyBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("cleanup"), PushValue.Boolean(true)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var result = Avm1Decompiler.DecompileMethod([
            new ActionTry(
                TryFlags.FinallyBlock,
                catchRegister: 0,
                catchVariable: string.Empty,
                tryBody,
                ReadOnlyMemory<byte>.Empty,
                finallyBody)
        ], swfVersion: 6);
        var source = result.ProjectSource();

        source.IsComplete.ShouldBeTrue();
        source.GetAs2Text().ShouldBe(string.Join(Environment.NewLine, [
            "try",
            "{",
            "    return;",
            "}",
            "finally",
            "{",
            "    cleanup = true;",
            "}",
            string.Empty
        ]));

        var region = result.Instructions.TryRegions.ShouldHaveSingleItem();
        result.ControlFlowGraph.TryGetBlockForAction(
            new ActionIndex(region.TryBodyStartAction.Value + 3),
            out var deadReadBlock).ShouldBeTrue();
        result.ControlFlowGraph.TryGetBlockForAction(
            region.FinallyBodyStartAction,
            out var finallyBlock).ShouldBeTrue();
        result.Reachability.IsReachable(deadReadBlock).ShouldBeFalse();
        result.Reachability.IsReachable(finallyBlock).ShouldBeTrue();
    }

    [Fact]
    public void Keeps_dynamic_stack_effect_diagnostics_as_information()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.Register(1)]),
            new ActionInitArray(),
            new ActionPop()
        ], swfVersion: 6);
        var source = result.ProjectSource();

        source.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1SRC005" &&
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Info &&
            diagnostic.Message.Contains(
                "Stack effect is dynamic",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_a_discarded_dynamic_read_in_a_class_initializer()
    {
        var result = Avm1Decompiler.DecompileMethod([
            new ActionPush([PushValue.String("obj")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionPop()
        ], swfVersion: 6);

        var initializer = Avm1ClassInitializerProjector.Project(
            result,
            "Demo",
            [],
            new Dictionary<(int Register, int Version), string>());

        initializer.ResidualStatements.Count.ShouldBe(1);
        initializer.Body.GetAs2Text().ShouldBe(
            $"obj.value;{Environment.NewLine}");
    }
}
