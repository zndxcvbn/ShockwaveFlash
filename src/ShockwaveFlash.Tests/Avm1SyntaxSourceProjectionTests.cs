using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Binding;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf2;
using ShockwaveFlash.Avm1.Swf3;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SyntaxSourceProjectionTests
{
    private static readonly string[] SkyUiSourceRoots =
    [
        @"C:\Users\artem\source\repos\skyui\src\CraftingMenu",
        @"C:\Users\artem\source\repos\skyui\src\Common",
        @"C:\Users\artem\source\repos\skyui\src\CLIK"
    ];

    [Fact]
    public void Projects_bound_classes_members_locals_and_interfaces_to_source_hir()
    {
        const string source = """
            import pkg.Widget;

            interface pkg.IRunner
            {
                function run(value:Number):Widget;
            }

            class pkg.Widget
            {
                public static var VERSION:Number = 1;
                public var value:Number;
            }

            class pkg.Consumer implements pkg.IRunner
            {
                private var peer:Widget;

                public function run(value:Number = 2):Widget
                {
                    var local:Widget = new Widget();
                    this.peer = local;
                    peer = local;
                    with (local)
                    {
                        peer = value;
                    }
                    Widget.VERSION;
                    return (value = +value, local);
                }
            }
            """;
        var projection = ProjectSingle(source);

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        projection.SourceProgram.IsComplete.ShouldBeTrue();
        projection.SourceProgram.SemanticAnalysis.IsComplete.ShouldBeTrue();
        var sourceFile = projection.SourceProgram.Files.ShouldHaveSingleItem();
        sourceFile.Classes.Count.ShouldBe(3);
        var sourceInterface = sourceFile.Classes.Single(type =>
            type.Name.Value == "pkg.IRunner");
        sourceInterface.Kind.ShouldBe(Avm1SourceTypeDeclarationKind.Interface);
        sourceInterface.Methods.ShouldHaveSingleItem().HasBody.ShouldBeFalse();
        var interfaceArtifact = new Avm1Compiler().CompileClass(
            sourceInterface,
            new Avm1ClassCompilationOptions(7));
        interfaceArtifact.Succeeded.ShouldBeTrue();
        interfaceArtifact.Methods.ShouldBeEmpty();

        var consumer = sourceFile.Classes.Single(type =>
            type.Name.Value == "pkg.Consumer");
        consumer.Interfaces.ShouldBe([
            new Avm1SourceQualifiedName("pkg.IRunner")
        ]);
        consumer.Fields.ShouldHaveSingleItem().DeclaredType!.Name.Value
            .ShouldBe("pkg.Widget");
        var program = projection.SourceProgram;
        var method = consumer.Methods.ShouldHaveSingleItem();
        program.TryGetBindings(method.Body.Arena, out var methodBindings)
            .ShouldBeTrue();
        method.DeclaredReturnType!.Name.Value.ShouldBe("pkg.Widget");
        var local = method.Body.Arena.Symbols.Single(symbol =>
            method.Body.Arena[symbol.Name] == "local");
        method.Body.Arena[local.DeclaredType].Name.IsValid.ShouldBeTrue();
        method.Body.Arena[method.Body.Arena[local.DeclaredType].Name]
            .ShouldBe("pkg.Widget");
        method.Body.Arena.Expressions.ShouldContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.Sequence);

        var peerBindings = method.Body.Arena.Expressions
            .Where(expression =>
                expression.Kind == Avm1SourceExpressionKind.DynamicName &&
                method.Body.Arena[expression.Name] == "peer")
            .Select(expression => (
                Binding: program.GetBinding(method.Body.Arena, expression.Index),
                IsDynamicScope: methodBindings.IsInDynamicScope(
                    expression.Index)))
            .ToArray();
        peerBindings.ShouldContain(item =>
            item.Binding.Kind == Avm1SourceBindingKind.Bound &&
            !item.IsDynamicScope);
        peerBindings.ShouldContain(item =>
            item.Binding.Kind == Avm1SourceBindingKind.Dynamic &&
            item.IsDynamicScope);
        method.Body.Arena.Expressions
            .Where(expression =>
                expression.Kind == Avm1SourceExpressionKind.QualifiedName &&
                method.Body.Arena[expression.Name] == "pkg.Widget")
            .ShouldAllBe(expression =>
                program.GetBinding(method.Body.Arena, expression.Index).IsBound);

        var artifact = new Avm1Compiler().CompileMethod(
            method.Body,
            new Avm1CompilationOptions(7));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));

        var emitted = sourceFile.GetAs2Text();
        emitted.ShouldContain("interface pkg.IRunner");
        emitted.ShouldContain("function run(value: Number): Widget;");
        emitted.ShouldContain("var local: Widget = new pkg.Widget();");
        emitted.ShouldContain("if (value === undefined)");
    }

    [Fact]
    public void Projects_all_supported_unary_forms_into_compilable_source_hir()
    {
        const string source = """
            class Test
            {
                function run(value:Number):Number
                {
                    void trace("side effect");
                    var bits:Number = ~value;
                    bits = -bits;
                    return +bits;
                }
            }
            """;
        var projection = ProjectSingle(source);
        var method = projection.SourceProgram.Files[0].Classes[0]
            .Methods.ShouldHaveSingleItem();

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        method.Body.Arena.Expressions
            .Where(expression => expression.Kind == Avm1SourceExpressionKind.Unary)
            .Select(expression => expression.Operator)
            .ShouldBe([
                Avm1SourceOperator.Void,
                Avm1SourceOperator.BitwiseNot,
                Avm1SourceOperator.UnaryMinus,
                Avm1SourceOperator.UnaryPlus
            ], ignoreOrder: true);
        var artifact = new Avm1Compiler().CompileMethod(
            method.Body,
            new Avm1CompilationOptions(7));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
    }

    [Fact]
    public void Projects_untyped_nested_function_names_without_parameter_indexing()
    {
        const string source = """
            class Test
            {
                function run(frame, label)
                {
                    function mark(value)
                    {
                        return value;
                    }
                    var lambda = function self(value)
                    {
                        return value;
                    };
                    return mark("a") + lambda("b");
                }
            }
            """;
        var projection = ProjectSingle(source);
        var method = projection.SourceProgram.Files[0].Classes[0]
            .Methods.ShouldHaveSingleItem();

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        method.Body.GetAs2Text().ShouldContain("function mark(value)");
        method.Body.GetAs2Text().ShouldContain("function self(value)");
        var artifact = new Avm1Compiler().CompileMethod(
            method.Body,
            new Avm1CompilationOptions(7));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
    }

    [Fact]
    public void Projects_unshadowed_global_conversions_and_trace_as_intrinsics()
    {
        const string source = """
            class Test
            {
                function run(value)
                {
                    trace(String(value));
                    return Number("4.5");
                }

                function shadow(String, value)
                {
                    return String(value);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var methods = projection.SourceProgram.Files[0].Classes[0].Methods;
        var run = methods.Single(method => method.Name == "run");
        var shadow = methods.Single(method => method.Name == "shadow");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Expressions
            .Where(expression => expression.Kind is
                Avm1SourceExpressionKind.IntrinsicCall)
            .Select(expression => run.Body.Arena[expression.Name])
            .ShouldBe(["String", "trace", "Number"], ignoreOrder: true);
        shadow.Body.Arena.Expressions.ShouldContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.Call);
        shadow.Body.Arena.Expressions.ShouldNotContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.IntrinsicCall);

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7));
        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Any(action => action is ActionToString).ShouldBeTrue();
        artifact.ActionBody.Actions
            .Any(action => action is ActionTrace).ShouldBeTrue();
        artifact.ActionBody.Actions
            .Any(action => action is ActionToNumber).ShouldBeTrue();
    }

    [Fact]
    public void Projects_unshadowed_timeline_functions_as_native_intrinsics()
    {
        const string source = """
            class Test
            {
                function run()
                {
                    nextFrame();
                    prevFrame();
                    play();
                    stop();
                    stopAllSounds();
                    toggleHighQuality();
                    call(frame);
                    gotoAndPlay(frame);
                    gotoAndStop(label);
                }

                function shadow(play, stop, toggleHighQuality, call, gotoAndPlay, gotoAndStop)
                {
                    play();
                    stop();
                    toggleHighQuality();
                    call(frame);
                    gotoAndPlay(12);
                    gotoAndStop("label");
                }
            }
            """;
        var projection = ProjectSingle(source);
        var methods = projection.SourceProgram.Files[0].Classes[0].Methods;
        var run = methods.Single(method => method.Name == "run");
        var shadow = methods.Single(method => method.Name == "shadow");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Expressions
            .Where(expression => expression.Kind is
                Avm1SourceExpressionKind.IntrinsicCall)
            .Select(expression => run.Body.Arena[expression.Name])
            .ShouldBe([
                "nextFrame",
                "prevFrame",
                "play",
                "stop",
                "stopAllSounds",
                "toggleHighQuality",
                "call",
                "gotoAndPlay",
                "gotoAndStop"
            ]);
        shadow.Body.Arena.Expressions.Count(expression =>
            expression.Kind is Avm1SourceExpressionKind.Call).ShouldBe(6);
        shadow.Body.Arena.Expressions.ShouldNotContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.IntrinsicCall);

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Take(6)
            .Select(action => action.GetType())
            .ShouldBe([
                typeof(ActionNextFrame),
                typeof(ActionPreviousFrame),
                typeof(ActionPlay),
                typeof(ActionStop),
                typeof(ActionStopSounds),
                typeof(ActionToggleQuality)
            ]);
        artifact.ActionBody.Actions.ShouldContain(action =>
            action is ActionCall);
        artifact.ActionBody.Actions
            .OfType<ActionGotoFrame2>()
            .Select(action => action.Play)
            .ShouldBe([true, false]);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action is ActionCallFunction);
    }

    [Fact]
    public void Rejects_malformed_legacy_timeline_intrinsic_arity()
    {
        const string source = """
            class Test
            {
                function run()
                {
                    toggleHighQuality(1);
                    call();
                }
            }
            """;
        var projection = ProjectSingle(source);
        var method = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(candidate => candidate.Name == "run");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        var artifact = new Avm1Compiler().CompileMethod(
            method.Body,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeFalse();
        artifact.Bytecode.IsEmpty.ShouldBeTrue();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains(
                "toggleHighQuality requires no arguments",
                StringComparison.Ordinal));
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains(
                "call requires one frame argument",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Projects_unshadowed_legacy_unary_globals_as_native_intrinsics()
    {
        const string source = """
            class Test
            {
                function run(value)
                {
                    trace(int(value));
                    trace(random(value));
                    trace(length(value));
                    trace(mblength(value));
                    trace(ord(value));
                    trace(chr(value));
                    trace(mbord(value));
                    trace(mbchr(value));
                }

                function shadow(int, random, length, mblength, ord, chr, mbord, mbchr)
                {
                    int(value);
                    random(value);
                    length(value);
                    mblength(value);
                    ord(value);
                    chr(value);
                    mbord(value);
                    mbchr(value);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var methods = projection.SourceProgram.Files[0].Classes[0].Methods;
        var run = methods.Single(method => method.Name == "run");
        var shadow = methods.Single(method => method.Name == "shadow");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Expressions
            .Where(expression => expression.Kind is
                Avm1SourceExpressionKind.IntrinsicCall)
            .Select(expression => run.Body.Arena[expression.Name])
            .Where(name => name != "trace")
            .ShouldBe([
                "int",
                "random",
                "length",
                "mblength",
                "ord",
                "chr",
                "mbord",
                "mbchr"
            ]);
        shadow.Body.Arena.Expressions.Count(expression =>
            expression.Kind is Avm1SourceExpressionKind.Call).ShouldBe(8);
        shadow.Body.Arena.Expressions.ShouldNotContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.IntrinsicCall);

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(4));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .Where(opcode => opcode is
                ActionOpcode.ToInteger or
                ActionOpcode.RandomNumber or
                ActionOpcode.StringLength or
                ActionOpcode.MBStringLength or
                ActionOpcode.CharToAscii or
                ActionOpcode.AsciiToChar or
                ActionOpcode.MBCharToAscii or
                ActionOpcode.MBAsciiToChar)
            .ShouldBe([
                ActionOpcode.ToInteger,
                ActionOpcode.RandomNumber,
                ActionOpcode.StringLength,
                ActionOpcode.MBStringLength,
                ActionOpcode.CharToAscii,
                ActionOpcode.AsciiToChar,
                ActionOpcode.MBCharToAscii,
                ActionOpcode.MBAsciiToChar
            ]);
    }

    [Fact]
    public void Projects_unshadowed_legacy_string_extract_globals_as_native_intrinsics()
    {
        const string source = """
            class Test
            {
                function run(value, start, count)
                {
                    trace(substring(value, start, count));
                    trace(mbsubstring(value, start, count));
                }

                function shadow(substring, mbsubstring)
                {
                    substring(value, start, count);
                    mbsubstring(value, start, count);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var methods = projection.SourceProgram.Files[0].Classes[0].Methods;
        var run = methods.Single(method => method.Name == "run");
        var shadow = methods.Single(method => method.Name == "shadow");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Expressions
            .Where(expression => expression.Kind is
                Avm1SourceExpressionKind.IntrinsicCall)
            .Select(expression => run.Body.Arena[expression.Name])
            .Where(name => name != "trace")
            .ShouldBe(["substring", "mbsubstring"]);
        shadow.Body.Arena.Expressions.Count(expression =>
            expression.Kind is Avm1SourceExpressionKind.Call).ShouldBe(2);
        shadow.Body.Arena.Expressions.ShouldNotContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.IntrinsicCall);

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(4));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .Where(opcode => opcode is
                ActionOpcode.StringExtract or
                ActionOpcode.MBStringExtract)
            .ShouldBe([
                ActionOpcode.StringExtract,
                ActionOpcode.MBStringExtract
            ]);
    }

    [Fact]
    public void Projects_unshadowed_legacy_host_globals_as_native_intrinsics()
    {
        const string source = """
            class Test
            {
                function run(target, property, value, name, depth)
                {
                    trace(getProperty(target, property));
                    setProperty(target, property, value);
                    duplicateMovieClip(target, name, depth);
                    removeMovieClip(name);
                    stopDrag();
                }

                function shadow(getProperty, setProperty, duplicateMovieClip, removeMovieClip, stopDrag)
                {
                    getProperty(target, property);
                    setProperty(target, property, value);
                    duplicateMovieClip(target, name, depth);
                    removeMovieClip(name);
                    stopDrag();
                }
            }
            """;
        var projection = ProjectSingle(source);
        var methods = projection.SourceProgram.Files[0].Classes[0].Methods;
        var run = methods.Single(method => method.Name == "run");
        var shadow = methods.Single(method => method.Name == "shadow");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Expressions
            .Where(expression => expression.Kind is
                Avm1SourceExpressionKind.IntrinsicCall)
            .Select(expression => run.Body.Arena[expression.Name])
            .Where(name => name != "trace")
            .ShouldBe([
                "getProperty",
                "setProperty",
                "duplicateMovieClip",
                "removeMovieClip",
                "stopDrag"
            ]);
        shadow.Body.Arena.Expressions.Count(expression =>
            expression.Kind is Avm1SourceExpressionKind.Call).ShouldBe(5);
        shadow.Body.Arena.Expressions.ShouldNotContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.IntrinsicCall);

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(4));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .Where(opcode => opcode is
                ActionOpcode.GetProperty or
                ActionOpcode.SetProperty or
                ActionOpcode.CloneSprite or
                ActionOpcode.RemoveSprite or
                ActionOpcode.EndDrag)
            .ShouldBe([
                ActionOpcode.GetProperty,
                ActionOpcode.SetProperty,
                ActionOpcode.CloneSprite,
                ActionOpcode.RemoveSprite,
                ActionOpcode.EndDrag
            ]);
    }

    [Fact]
    public void Compiles_both_start_drag_stack_shapes_as_native_intrinsics()
    {
        const string source = """
            class Test
            {
                function run(target, lockCenter, left, top, right, bottom)
                {
                    startDrag(target, lockCenter);
                    startDrag(target, lockCenter, left, top, right, bottom);
                }

                function shadow(startDrag)
                {
                    startDrag(target, lockCenter);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var methods = projection.SourceProgram.Files[0].Classes[0].Methods;
        var run = methods.Single(method => method.Name == "run");
        var shadow = methods.Single(method => method.Name == "shadow");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Expressions
            .Where(expression => expression.Kind is
                Avm1SourceExpressionKind.IntrinsicCall)
            .Select(expression => run.Body.Arena[expression.Name])
            .ShouldBe(["startDrag", "startDrag"]);
        shadow.Body.Arena.Expressions.Count(expression =>
            expression.Kind is Avm1SourceExpressionKind.Call).ShouldBe(1);
        shadow.Body.Arena.Expressions.ShouldNotContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.IntrinsicCall);

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(5));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action is ActionStartDrag).ShouldBe(2);
        artifact.ActionBody.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Compiles_literal_timeline_targets_to_adobe_legacy_actions()
    {
        const string source = """
            class Test
            {
                function run()
                {
                    gotoAndPlay(12);
                    gotoAndStop("done");
                }
            }
            """;
        var projection = ProjectSingle(source);
        var run = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(method => method.Name == "run");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .Select(action => action.Opcode)
            .ShouldBe([
                ActionOpcode.GotoFrame,
                ActionOpcode.Play,
                ActionOpcode.GoToLabel
            ]);
        artifact.ActionBody.Actions[0].ShouldBeOfType<ActionGotoFrame>()
            .Frame.ShouldBe((ushort)11);
        artifact.ActionBody.Actions[2].ShouldBeOfType<ActionGoToLabel>()
            .Label.ShouldBe("done");
    }

    [Fact]
    public void Rejects_scene_timeline_calls_until_scene_layout_is_available()
    {
        const string source = """
            class Test
            {
                function run()
                {
                    gotoAndPlay("Scene 2", 3);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var run = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(method => method.Name == "run");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains(
                "scene-layout metadata",
                StringComparison.Ordinal));
        artifact.ActionBody.ShouldBeNull();
    }

    [Fact]
    public void Compiles_scene_timeline_calls_with_explicit_scene_bias()
    {
        const string source = """
            class Test
            {
                function run(frame)
                {
                    gotoAndPlay("Scene 2", 3);
                    gotoAndStop("Scene 1", "intro");
                    gotoAndPlay("Scene 2", frame);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var run = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(method => method.Name == "run");
        var layout = new Avm1TimelineLayout(
            frameCount: 20,
            scenes:
            [
                new Avm1TimelineScene("Scene 1", 0),
                new Avm1TimelineScene("Scene 2", 10)
            ]);

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7)
            {
                TimelineLayout = layout,
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var gotos = artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionGotoFrame2>()
            .ToArray();
        gotos.Select(action => action.Play).ShouldBe([true, false, true]);
        gotos.Select(action => action.HasSceneBias).ShouldBe([true, true, true]);
        gotos.Select(action => action.SceneBias).ShouldBe([(ushort)10, 0, 10]);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action is ActionGotoFrame);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action is ActionGoToLabel);
    }

    [Fact]
    public void Rejects_a_scene_name_missing_from_the_timeline_layout()
    {
        const string source = """
            class Test
            {
                function run()
                {
                    gotoAndPlay("Missing", 1);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var run = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(method => method.Name == "run");
        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7)
            {
                TimelineLayout = new Avm1TimelineLayout(
                    frameCount: 10,
                    scenes: [new Avm1TimelineScene("Scene 1", 0)])
            });

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains(
                "missing or ambiguous",
                StringComparison.Ordinal));
        artifact.ActionBody.ShouldBeNull();
    }

    [Fact]
    public void Compiles_if_frame_loaded_to_immediate_and_stack_wait_actions()
    {
        const string source = """
            class Test
            {
                function run(frame)
                {
                    ifFrameLoaded(12)
                    {
                        trace("ready");
                    }
                    ifFrameLoaded(frame)
                    {
                        stop();
                    }
                }
            }
            """;
        var projection = ProjectSingle(source);
        var run = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(method => method.Name == "run");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Statements.Count(statement => statement.Kind is
            Avm1SourceStatementKind.IfFrameLoaded).ShouldBe(2);
        run.Body.GetAs2Text().ShouldContain("ifFrameLoaded(12)");
        run.Body.GetAs2Text().ShouldContain("ifFrameLoaded(frame)");

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        var actions = artifact.ActionBody.ShouldNotBeNull().Actions;
        var immediate = actions.OfType<ActionWaitForFrame>().Single();
        var stack = actions.OfType<ActionWaitForFrame2>().Single();
        immediate.Frame.ShouldBe((ushort)11);
        immediate.SkipCount.ShouldBeGreaterThan((byte)0);
        stack.SkipCount.ShouldBeGreaterThan((byte)0);
    }

    [Fact]
    public void Resolves_if_frame_loaded_scene_targets_to_absolute_frames()
    {
        const string source = """
            class Test
            {
                function run()
                {
                    ifFrameLoaded("Scene 2", 3)
                    {
                        play();
                    }
                }
            }
            """;
        var projection = ProjectSingle(source);
        var run = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(method => method.Name == "run");
        var layout = new Avm1TimelineLayout(
            frameCount: 20,
            scenes:
            [
                new Avm1TimelineScene("Scene 1", 0),
                new Avm1TimelineScene("Scene 2", 10)
            ]);

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7)
            {
                TimelineLayout = layout,
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionWaitForFrame>()
            .Single()
            .Frame.ShouldBe((ushort)12);
    }

    [Fact]
    public void Rejects_dynamic_scene_relative_if_frame_loaded_targets()
    {
        const string source = """
            class Test
            {
                function run(frame)
                {
                    ifFrameLoaded("Scene 2", frame)
                    {
                        play();
                    }
                }
            }
            """;
        var projection = ProjectSingle(source);
        var run = projection.SourceProgram.Files[0].Classes[0].Methods
            .Single(method => method.Name == "run");
        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7)
            {
                TimelineLayout = new Avm1TimelineLayout(
                    frameCount: 20,
                    scenes:
                    [
                        new Avm1TimelineScene("Scene 1", 0),
                        new Avm1TimelineScene("Scene 2", 10)
                    ])
            });

        artifact.Succeeded.ShouldBeFalse();
        artifact.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Message.Contains(
                "numeric literal or frame-label literal",
                StringComparison.Ordinal));
        artifact.ActionBody.ShouldBeNull();
    }

    [Fact]
    public void Projects_unshadowed_url_functions_as_native_intrinsics()
    {
        const string source = """
            class Test
            {
                function run(url, target, level, command, parameters)
                {
                    getURL(url, target, "POST");
                    fscommand(command, parameters);
                    loadMovie(url, target);
                    loadMovieNum(url, level);
                    loadVariables(url, target, "GET");
                    loadVariablesNum(url, level, "POST");
                }

                function shadow(getURL, fscommand, loadMovie, loadMovieNum,
                    loadVariables, loadVariablesNum)
                {
                    getURL("https://example.test");
                    fscommand("quit");
                    loadMovie("movie.swf", target);
                    loadMovieNum("movie.swf", 2);
                    loadVariables("variables.txt", target);
                    loadVariablesNum("variables.txt", 2);
                }
            }
            """;
        var projection = ProjectSingle(source);
        var methods = projection.SourceProgram.Files[0].Classes[0].Methods;
        var run = methods.Single(method => method.Name == "run");
        var shadow = methods.Single(method => method.Name == "shadow");

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        run.Body.Arena.Expressions
            .Where(expression => expression.Kind is
                Avm1SourceExpressionKind.IntrinsicCall)
            .Select(expression => run.Body.Arena[expression.Name])
            .ShouldBe([
                "getURL",
                "fscommand",
                "loadMovie",
                "loadMovieNum",
                "loadVariables",
                "loadVariablesNum"
            ]);
        shadow.Body.Arena.Expressions.Count(expression =>
            expression.Kind is Avm1SourceExpressionKind.Call).ShouldBe(6);
        shadow.Body.Arena.Expressions.ShouldNotContain(expression =>
            expression.Kind == Avm1SourceExpressionKind.IntrinsicCall);

        var artifact = new Avm1Compiler().CompileMethod(
            run.Body,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionGetURL2>()
            .Select(action => action.Flags)
            .ShouldBe([
                GetUrlFlags.MethodPost,
                GetUrlFlags.MethodNone,
                GetUrlFlags.LoadTarget,
                GetUrlFlags.MethodNone,
                GetUrlFlags.MethodGet |
                    GetUrlFlags.LoadTarget |
                    GetUrlFlags.LoadVariables,
                GetUrlFlags.MethodPost | GetUrlFlags.LoadVariables
            ]);
        artifact.ActionBody.Actions.Count(action => action is ActionStringAdd)
            .ShouldBe(2);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action is ActionCallFunction);
    }

    [Fact]
    public void Compiles_immediate_dynamic_and_nested_tell_target_scopes()
    {
        const string source = """
            class Test
            {
                function run(target, inner)
                {
                    tellTarget("menu")
                    {
                        trace("static");
                    }
                    tellTarget(target)
                    {
                        tellTarget(inner)
                        {
                            trace("nested");
                        }
                    }
                }
            }
            """;
        var projection = ProjectSingle(source);
        var method = projection.SourceProgram.Files[0].Classes[0]
            .Methods.ShouldHaveSingleItem();

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        method.Body.Arena.Statements.Count(statement =>
            statement.Kind == Avm1SourceStatementKind.TellTarget).ShouldBe(3);
        method.Body.GetAs2Text().ShouldContain("tellTarget(\"menu\")");
        method.Body.GetAs2Text().ShouldContain("tellTarget(target)");

        var artifact = new Avm1Compiler().CompileMethod(
            method.Body,
            new Avm1CompilationOptions(7)
            {
                OptimizationLevel = Avm1OptimizationLevel.Basic
            });

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions.Count(action =>
            action is ActionSetTarget).ShouldBe(3);
        artifact.ActionBody.Actions.Count(action =>
            action is ActionSetTarget2).ShouldBe(3);
        artifact.ActionBody.Actions.OfType<ActionSetTarget>()
            .Select(action => action.TargetName)
            .ShouldBe(["menu", string.Empty, string.Empty]);

        var recoveredMethod = Avm1Decompiler.DecompileMethod(
            artifact.ActionBody.Actions,
            swfVersion: 7);
        var recovered = recoveredMethod.ProjectSource();
        recovered.IsComplete.ShouldBeTrue(string.Join(
            Environment.NewLine,
            recovered.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")) +
            Environment.NewLine + recoveredMethod.GetStructuredAstDebugText() +
            Environment.NewLine + recovered.GetAs2Text());
        recovered.Arena.Statements.Count(statement =>
            statement.Kind == Avm1SourceStatementKind.TellTarget).ShouldBe(3);
    }

    [Fact]
    public void Compiles_tell_target_control_exits_with_parent_restoration()
    {
        const string source = """
            class Test
            {
                function run(condition, skip)
                {
                    tellTarget("outer")
                    {
                        while (condition)
                        {
                            tellTarget("inner")
                            {
                                if (skip)
                                {
                                    continue;
                                }
                                break;
                            }
                        }
                    }
                }
            }
            """;
        var projection = ProjectSingle(source);
        var method = projection.SourceProgram.Files[0].Classes[0]
            .Methods.ShouldHaveSingleItem();

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        var artifact = new Avm1Compiler().CompileMethod(
            method.Body,
            new Avm1CompilationOptions(7));

        artifact.Succeeded.ShouldBeTrue(Describe(artifact));
        artifact.ActionBody.ShouldNotBeNull().Actions
            .OfType<ActionSetTarget>()
            .Select(action => action.TargetName)
            .ShouldBe(["outer", "inner", "outer", "outer", string.Empty]);
        artifact.ActionBody.Actions.ShouldNotContain(action =>
            action is ActionSetTarget2);
    }

    [Fact]
    public void Resolves_external_wildcard_types_members_and_interfaces()
    {
        const string source = """
            import flash.geom.*;

            class pkg.Consumer
            {
                function getWidth(rect:Rectangle):Number
                {
                    return rect.width;
                }
            }
            """;
        var projection = ProjectSingle(source, Avm1ReferenceCatalogs.Default);

        projection.HasErrors.ShouldBeFalse(Describe(projection));
        var method = projection.SourceProgram.Files[0].Classes[0]
            .Methods.ShouldHaveSingleItem();
        var parameter = method.Body.Arena[method.Body.Parameters[0]];
        var parameterType = method.Body.Arena[parameter.DeclaredType];
        method.Body.Arena[parameterType.Name]
            .ShouldBe("flash.geom.Rectangle");
        var member = method.Body.Arena.Expressions.Single(expression =>
            expression.Kind is Avm1SourceExpressionKind.MemberAccess);
        var binding = projection.SourceProgram.GetBinding(
            method.Body.Arena,
            member.Index);
        binding.IsBound.ShouldBeTrue();
        projection.SourceProgram[binding.Symbol].QualifiedName.Value
            .ShouldBe("flash.geom.Rectangle.width");
        var returnFact = projection.SourceProgram.TypeAnalysis
            .GetMethodReturnFact(method.Body);
        projection.SourceProgram[returnFact.Type].Kind
            .ShouldBe(Avm1SourceTypeKind.Number);
        projection.SourceProgram.TryGetClassSymbol(
            "skyui.components.list.IEntryFormatter",
            out var interfaceSymbol).ShouldBeTrue();
        projection.SourceProgram[interfaceSymbol].Kind
            .ShouldBe(Avm1SourceProgramSymbolKind.Interface);
        projection.SourceProgram[interfaceSymbol].Flags
            .HasFlag(Avm1SourceProgramSymbolFlags.External).ShouldBeTrue();
    }

    [Fact]
    public void Projects_complete_skyui_syntax_corpus_with_only_include_blockers()
    {
        var sourceFiles = SkyUiSourceRoots
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.as",
                SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new Avm1SyntaxSourceFile(
                path,
                Avm1SyntaxTree.Parse(File.ReadAllText(path))))
            .ToArray();
        sourceFiles.Length.ShouldBe(110);
        var syntaxProgram = Avm1SyntaxProgram.Create(sourceFiles);
        var lexicalBinding = Avm1SyntaxLexicalBinding.Bind(syntaxProgram);

        var projection = Avm1SyntaxSourceProjector.Project(
            syntaxProgram,
            lexicalBinding);

        syntaxProgram.HasErrors.ShouldBeFalse();
        lexicalBinding.HasErrors.ShouldBeFalse();
        projection.SourceProgram.Files.Count.ShouldBe(110);
        projection.SourceProgram.Files
            .SelectMany(file => file.Classes)
            .Count().ShouldBe(110);
        projection.SourceProgram.Files
            .SelectMany(file => file.Classes)
            .Count(type => type.Kind == Avm1SourceTypeDeclarationKind.Interface)
            .ShouldBe(3);
        projection.SourceProgram.IsComplete.ShouldBeTrue(Describe(projection));
        projection.SourceProgram.SemanticAnalysis.IsComplete
            .ShouldBeTrue(Describe(projection));
        projection.Diagnostics
            .Where(diagnostic =>
                diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.Code)
            .ShouldBe(["AVM1P1003", "AVM1P1003"], ignoreOrder: true);
        projection.Diagnostics
            .Where(diagnostic =>
                diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Warning)
            .Select(diagnostic => diagnostic.Code)
            .ShouldBe(["AVM1P1009"]);

        var compiler = new Avm1Compiler();
        var methodOptions = new Avm1CompilationOptions(7)
        {
            RegisterFile = Avm1CompilationRegisterFile.DefineFunction2,
            RegisterCount = byte.MaxValue,
            ReservedTemporaryRegisters = new Avm1TemporaryRegisterRange(
                1,
                byte.MaxValue - 1)
        };
        var compilationFailures = new List<string>();
        var compiledMethodCount = 0;
        foreach (var type in projection.SourceProgram.Files
                     .SelectMany(file => file.Classes))
        {
            foreach (var method in type.Methods.Where(method => method.HasBody))
            {
                compiledMethodCount++;
                var artifact = compiler.CompileMethod(
                    method.Body,
                    methodOptions);
                if (artifact.Succeeded)
                    continue;
                compilationFailures.Add(
                    $"{type.Name.Value}.{method.Name} ({method.Kind}): " +
                    Describe(artifact));
            }
        }
        compiledMethodCount.ShouldBe(975);
        compilationFailures.ShouldBeEmpty(string.Join(
            Environment.NewLine,
            compilationFailures.Take(20)));

        foreach (var symbol in syntaxProgram.Symbols)
        {
            if (symbol.Kind is
                Avm1SyntaxProgramSymbolKind.Class or
                Avm1SyntaxProgramSymbolKind.Interface)
            {
                projection.TryGetType(symbol.Index, out _).ShouldBeTrue();
            }
            else
                projection.TryGetMember(symbol.Index, out _).ShouldBeTrue();
        }
    }

    private static Avm1SyntaxSourceProjection ProjectSingle(
        string source,
        IAvm1ReferenceProvider? references = null)
    {
        var tree = Avm1SyntaxTree.Parse(source);
        tree.HasErrors.ShouldBeFalse(string.Join(
            Environment.NewLine,
            tree.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} {diagnostic.Span}: {diagnostic.Message}")));
        var program = Avm1SyntaxProgram.Create([
            new Avm1SyntaxSourceFile("Test.as", tree)
        ]);
        return Avm1SyntaxSourceProjector.Project(
            program,
            referenceProvider: references);
    }

    private static string Describe(Avm1SyntaxSourceProjection projection) =>
        string.Join(
            Environment.NewLine,
            projection.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} {diagnostic.File.Value}:{diagnostic.Span} " +
                diagnostic.Message));

    private static string Describe(Avm1MethodArtifact artifact) =>
        string.Join(
            Environment.NewLine,
            artifact.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));
}
