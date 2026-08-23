using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SourceProjectionPlanTests
{
    [Fact]
    public void Synthesizes_imports_and_shortens_only_bound_class_references()
    {
        var fixture = CreateWidgetConsumerFixture(includeNestedFunction: true);
        var programProjection = fixture.Program.CreateProjection();
        var projection = programProjection.GetFile(fixture.ConsumerFile);

        projection.Imports.Select(import => import.Value).ShouldBe(["pkg.Widget"]);
        projection.ExpressionSpellingCount.ShouldBe(2);
        var projectedText = projection.GetAs2Text();
        projectedText.ShouldContain("import pkg.Widget;");
        projectedText.ShouldContain("class pkg.Consumer extends Widget");
        projectedText.ShouldContain("var peer: Widget;");
        projectedText.ShouldContain("result = Widget.VALUE;");
        projectedText.ShouldContain("return Widget.VALUE;");

        var faithfulText = fixture.ConsumerFile.GetAs2Text();
        faithfulText.ShouldNotContain("import pkg.Widget;");
        faithfulText.ShouldContain("result = pkg.Widget.VALUE;");
        faithfulText.ShouldContain("return pkg.Widget.VALUE;");
    }

    [Fact]
    public void Keeps_qualified_reference_when_a_local_uses_the_import_name()
    {
        var method = Project([
            new ActionPush([PushValue.String("Widget"), PushValue.Integer(1)]),
            new ActionDefineLocal(),
            .. BuildStaticRead("pkg.Widget", "result")
        ]);
        var fixture = CreateWidgetConsumerFixture(method);
        var projection = fixture.Program.CreateProjection().GetFile(fixture.ConsumerFile);

        projection.Imports.ShouldBeEmpty();
        projection.GetAs2Text().ShouldContain("result = pkg.Widget.VALUE;");
    }

    [Fact]
    public void Keeps_qualified_reference_when_an_unresolved_dynamic_name_would_change_binding()
    {
        var method = Project([
            new ActionPush([PushValue.String("sink"), PushValue.String("Widget")]),
            new ActionGetVariable(),
            new ActionSetVariable(),
            .. BuildStaticRead("pkg.Widget", "result")
        ]);
        var fixture = CreateWidgetConsumerFixture(method);
        var projection = fixture.Program.CreateProjection().GetFile(fixture.ConsumerFile);

        projection.Imports.ShouldBeEmpty();
        projection.GetAs2Text().ShouldContain("sink = Widget;");
        projection.GetAs2Text().ShouldContain("result = pkg.Widget.VALUE;");
    }

    [Fact]
    public void Does_not_import_two_referenced_classes_with_the_same_simple_name()
    {
        var method = Project([
            .. BuildStaticRead("a.Widget", "first"),
            .. BuildStaticRead("b.Widget", "second")
        ]);
        var consumer = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceMethodDeclaration(
                    "probe",
                    Avm1SourceMethodKind.Method,
                    method)
            ]);
        var consumerFile = new Avm1SourceFile([consumer]);
        var program = Avm1SourceProgram.Create([
            CreateClassFile(CreateWidgetClass("a.Widget")),
            CreateClassFile(CreateWidgetClass("b.Widget")),
            consumerFile
        ]);
        var projection = program.CreateProjection().GetFile(consumerFile);

        projection.Imports.ShouldBeEmpty();
        projection.GetAs2Text().ShouldContain("first = a.Widget.VALUE;");
        projection.GetAs2Text().ShouldContain("second = b.Widget.VALUE;");
    }

    [Fact]
    public void Ambiguous_class_binding_does_not_create_an_import()
    {
        var consumer = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceMethodDeclaration(
                    "probe",
                    Avm1SourceMethodKind.Method,
                    Project(BuildStaticRead("pkg.Widget", "result")))
            ]);
        var consumerFile = CreateClassFile(consumer);
        var program = Avm1SourceProgram.Create([
            CreateClassFile(CreateWidgetClass("pkg.Widget")),
            CreateClassFile(CreateWidgetClass("pkg.Widget")),
            consumerFile
        ]);
        var projection = program.CreateProjection().GetFile(consumerFile);

        projection.Imports.ShouldBeEmpty();
        projection.ExpressionSpellingCount.ShouldBe(0);
        projection.GetAs2Text().ShouldContain("result = pkg.Widget.VALUE;");
    }

    [Fact]
    public void Does_not_shorten_a_runtime_class_slot_assignment_target()
    {
        var method = Project([
            new ActionPush([PushValue.String("pkg")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Widget"), PushValue.Boolean(true)]),
            new ActionSetMember()
        ]);
        var consumerFile = CreateClassFile(new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceMethodDeclaration(
                    "probe",
                    Avm1SourceMethodKind.Method,
                    method)
            ]));
        var program = Avm1SourceProgram.Create([
            CreateClassFile(CreateWidgetClass("pkg.Widget")),
            consumerFile
        ]);
        var projection = program.CreateProjection().GetFile(consumerFile);

        projection.Imports.ShouldBeEmpty();
        projection.GetAs2Text().ShouldContain("pkg.Widget = true;");
    }

    [Fact]
    public void Existing_import_enables_shortening_without_import_synthesis()
    {
        var fixture = CreateWidgetConsumerFixture(includeNestedFunction: false);
        var consumerFile = new Avm1SourceFile(
            fixture.ConsumerFile.Classes,
            [new Avm1SourceQualifiedName("pkg.Widget")]);
        var program = Avm1SourceProgram.Create([
            fixture.WidgetFile,
            consumerFile
        ]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            SynthesizeImports = false
        }).GetFile(consumerFile);

        projection.Imports.Select(import => import.Value).ShouldBe(["pkg.Widget"]);
        projection.GetAs2Text().ShouldContain("result = Widget.VALUE;");
    }

    [Fact]
    public void Elides_only_proven_this_field_and_accessor_references()
    {
        var method = Project([
            .. BuildThisMemberRead("value", "first"),
            .. BuildThisMemberWrite("value"),
            .. BuildThisMemberRead("status", "second"),
            .. BuildThisMemberWrite("status")
        ]);
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Widget"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceField("value"),
                new Avm1SourceMethodDeclaration(
                    "status",
                    Avm1SourceMethodKind.Getter,
                    Project([])),
                new Avm1SourceMethodDeclaration(
                    "status",
                    Avm1SourceMethodKind.Setter,
                    Project([])),
                new Avm1SourceMethodDeclaration(
                    "probe",
                    Avm1SourceMethodKind.Method,
                    method)
            ]);
        var sourceFile = CreateClassFile(sourceClass);
        var program = Avm1SourceProgram.Create([sourceFile]);

        var faithfulProjection = program.CreateProjection().GetFile(sourceFile);
        faithfulProjection.ThisElisionCount.ShouldBe(0);
        faithfulProjection.GetAs2Text().ShouldContain("first = this.value;");
        faithfulProjection.GetAs2Text().ShouldContain("second = this.status;");

        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);
        projection.ThisElisionCount.ShouldBe(4);
        var text = projection.GetAs2Text();
        text.ShouldContain("first = value;");
        text.ShouldContain("value = true;");
        text.ShouldContain("second = status;");
        text.ShouldContain("status = true;");
        text.ShouldNotContain("this.value");
        text.ShouldNotContain("this.status");
    }

    [Fact]
    public void Keeps_this_when_a_local_shadows_the_member_name()
    {
        var method = Project([
            new ActionPush([PushValue.String("value"), PushValue.Integer(1)]),
            new ActionDefineLocal(),
            .. BuildThisMemberRead("value", "result")
        ]);
        var sourceFile = CreateInstanceFieldFile(method);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(0);
        projection.GetAs2Text().ShouldContain("result = this.value;");
    }

    [Fact]
    public void Ignores_shadowing_and_eval_in_an_unrelated_nested_activation()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("value"), PushValue.Integer(1)]),
            new ActionDefineLocal(),
            new ActionPush([PushValue.Integer(0), PushValue.String("eval")]),
            new ActionCallFunction(),
            new ActionPop()
        ], swfVersion: 7);
        var method = Project([
            new ActionPush([PushValue.String("callback")]),
            new ActionDefineFunction2(
                string.Empty,
                registerCount: 0,
                flags: (FunctionFlags)0,
                parameters: [],
                body: functionBody),
            new ActionSetVariable(),
            .. BuildThisMemberRead("value", "result")
        ]);
        var sourceFile = CreateInstanceFieldFile(method);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(1);
        projection.GetAs2Text().ShouldContain("var value = 1;");
        projection.GetAs2Text().ShouldContain("eval();");
        projection.GetAs2Text().ShouldContain("result = value;");
        projection.GetAs2Text().ShouldNotContain("result = this.value;");
    }

    [Fact]
    public void Applies_catch_shadowing_only_inside_the_catch_scope()
    {
        var tryBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x"), PushValue.Integer(1)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection(
            BuildThisMemberRead("value", "caughtValue"),
            swfVersion: 6);
        var method = Avm1Decompiler.DecompileMethod([
            new ActionTry(
                TryFlags.CatchBlock,
                catchRegister: 0,
                catchVariable: "value",
                tryBody,
                catchBody,
                ReadOnlyMemory<byte>.Empty),
            .. BuildThisMemberRead("value", "result")
        ], swfVersion: 6).ProjectSource();
        var sourceFile = CreateInstanceFieldFile(method);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(1);
        projection.GetAs2Text().ShouldContain("caughtValue = this.value;");
        projection.GetAs2Text().ShouldContain("result = value;");
    }

    [Fact]
    public void Keeps_this_inside_with_dynamic_scope()
    {
        var withBody = Avm1Action.EncodeCollection(
            BuildThisMemberRead("value", "result"),
            swfVersion: 7);
        var method = Project([
            new ActionPush([PushValue.String("scope")]),
            new ActionGetVariable(),
            new ActionWith(withBody)
        ]);
        var sourceFile = CreateInstanceFieldFile(method);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(0);
        projection.GetAs2Text().ShouldContain("result = this.value;");
    }

    [Fact]
    public void Keeps_this_in_an_arena_that_calls_eval()
    {
        var method = Project([
            new ActionPush([PushValue.Integer(0), PushValue.String("eval")]),
            new ActionCallFunction(),
            new ActionPop(),
            .. BuildThisMemberRead("value", "result")
        ]);
        var sourceFile = CreateInstanceFieldFile(method);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(0);
        projection.GetAs2Text().ShouldContain("eval();");
        projection.GetAs2Text().ShouldContain("result = this.value;");
    }

    [Fact]
    public void Keeps_this_in_an_arena_with_a_computed_eval_lookup()
    {
        var method = Project([
            new ActionPush([PushValue.String("name")]),
            new ActionGetVariable(),
            new ActionGetVariable(),
            new ActionPop(),
            .. BuildThisMemberRead("value", "result")
        ]);
        var sourceFile = CreateInstanceFieldFile(method);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(0);
        projection.GetAs2Text().ShouldContain("eval(name);");
        projection.GetAs2Text().ShouldContain("result = this.value;");
    }

    [Fact]
    public void Elides_method_call_but_keeps_function_valued_field_receiver()
    {
        var probe = Project([
            new ActionPush([PushValue.Integer(0), PushValue.String("this")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("helper")]),
            new ActionCallMethod(),
            new ActionPop(),
            new ActionPush([PushValue.Integer(0), PushValue.String("this")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("callback")]),
            new ActionCallMethod(),
            new ActionPop()
        ]);
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Widget"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceField("callback"),
                new Avm1SourceMethodDeclaration(
                    "helper",
                    Avm1SourceMethodKind.Method,
                    Project([])),
                new Avm1SourceMethodDeclaration(
                    "probe",
                    Avm1SourceMethodKind.Method,
                    probe)
            ]);
        var sourceFile = CreateClassFile(sourceClass);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(1);
        projection.GetAs2Text().ShouldContain("helper();");
        projection.GetAs2Text().ShouldContain("this.callback();");
    }

    [Fact]
    public void Keeps_computed_this_member_access()
    {
        var method = Project([
            new ActionPush([PushValue.String("result"), PushValue.String("this")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("key")]),
            new ActionGetVariable(),
            new ActionGetMember(),
            new ActionSetVariable()
        ]);
        var sourceFile = CreateInstanceFieldFile(method);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var projection = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            ElideThisReferences = true
        }).GetFile(sourceFile);

        projection.ThisElisionCount.ShouldBe(0);
        projection.GetAs2Text().ShouldContain("result = this[key];");
    }

    private static ProgramFixture CreateWidgetConsumerFixture(bool includeNestedFunction)
    {
        var probe = Project(BuildStaticRead("pkg.Widget", "result"));
        var members = new List<Avm1SourceClassMember>
        {
            new Avm1SourceField(
                "peer",
                declaredType: new Avm1SourceTypeReference(
                    new Avm1SourceQualifiedName("pkg.Widget"))),
            new Avm1SourceMethodDeclaration(
                "probe",
                Avm1SourceMethodKind.Method,
                probe)
        };
        if (includeNestedFunction)
            members.Add(CreateNestedProbe());
        return CreateWidgetConsumerFixture(members);
    }

    private static ProgramFixture CreateWidgetConsumerFixture(Avm1SourceMethod method) =>
        CreateWidgetConsumerFixture([
            new Avm1SourceMethodDeclaration(
                "probe",
                Avm1SourceMethodKind.Method,
                method)
        ]);

    private static ProgramFixture CreateWidgetConsumerFixture(
        IEnumerable<Avm1SourceClassMember> members)
    {
        var widgetClass = CreateWidgetClass("pkg.Widget");
        var widgetFile = CreateClassFile(widgetClass);
        var consumerClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            new Avm1SourceQualifiedName("pkg.Widget"),
            interfaces: null,
            members);
        var consumerFile = CreateClassFile(consumerClass);
        var program = Avm1SourceProgram.Create([widgetFile, consumerFile]);
        return new ProgramFixture(program, widgetFile, consumerFile);
    }

    private static Avm1SourceMethodDeclaration CreateNestedProbe()
    {
        var bodyActions = BuildClassValue("pkg.Widget").ToList();
        bodyActions.Add(new ActionReturn());
        var functionBody = Avm1Action.EncodeCollection(bodyActions, swfVersion: 7);
        return new Avm1SourceMethodDeclaration(
            "nestedProbe",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("callback")]),
                new ActionDefineFunction2(
                    string.Empty,
                    registerCount: 0,
                    flags: (FunctionFlags)0,
                    parameters: [],
                    body: functionBody),
                new ActionSetVariable()
            ]));
    }

    private static Avm1SourceClass CreateWidgetClass(string name) =>
        new(
            new Avm1SourceQualifiedName(name),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceField(
                    "VALUE",
                    Avm1SourceDeclarationModifiers.Static,
                    declaredType: new Avm1SourceTypeReference(
                        new Avm1SourceQualifiedName("Number")))
            ]);

    private static Avm1SourceFile CreateInstanceFieldFile(Avm1SourceMethod method) =>
        CreateClassFile(new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Widget"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceField("value"),
                new Avm1SourceMethodDeclaration(
                    "probe",
                    Avm1SourceMethodKind.Method,
                    method)
            ]));

    private static Avm1SourceFile CreateClassFile(Avm1SourceClass sourceClass) =>
        new([sourceClass]);

    private static Avm1SourceMethod Project(IReadOnlyList<Avm1Action> actions) =>
        Avm1Decompiler.DecompileMethod(actions, swfVersion: 7).ProjectSource();

    private static IReadOnlyList<Avm1Action> BuildStaticRead(
        string className,
        string targetName)
    {
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String(targetName)])
        };
        actions.AddRange(BuildClassValue(className));
        actions.Add(new ActionSetVariable());
        return actions;
    }

    private static IReadOnlyList<Avm1Action> BuildClassValue(string className)
    {
        var segments = className.Split('.');
        var actions = new List<Avm1Action>
        {
            new ActionPush([PushValue.String(segments[0])]),
            new ActionGetVariable()
        };
        for (var i = 1; i < segments.Length; i++)
        {
            actions.Add(new ActionPush([PushValue.String(segments[i])]));
            actions.Add(new ActionGetMember());
        }
        actions.Add(new ActionPush([PushValue.String("VALUE")]));
        actions.Add(new ActionGetMember());
        return actions;
    }

    private static IReadOnlyList<Avm1Action> BuildThisMemberRead(
        string memberName,
        string targetName) =>
    [
        new ActionPush([PushValue.String(targetName)]),
        new ActionPush([PushValue.String("this")]),
        new ActionGetVariable(),
        new ActionPush([PushValue.String(memberName)]),
        new ActionGetMember(),
        new ActionSetVariable()
    ];

    private static IReadOnlyList<Avm1Action> BuildThisMemberWrite(string memberName) =>
    [
        new ActionPush([PushValue.String("this")]),
        new ActionGetVariable(),
        new ActionPush([PushValue.String(memberName), PushValue.Boolean(true)]),
        new ActionSetMember()
    ];

    private sealed record ProgramFixture(
        Avm1SourceProgram Program,
        Avm1SourceFile WidgetFile,
        Avm1SourceFile ConsumerFile);
}
