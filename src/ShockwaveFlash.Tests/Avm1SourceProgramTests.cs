using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Types;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SourceProgramTests
{
    [Fact]
    public void Interns_declarations_and_nominal_types_across_method_arenas()
    {
        var fixture = CreateProgramFixture();
        var program = fixture.Program;

        program.Files.Count.ShouldBe(2);
        program.IsComplete.ShouldBeTrue();
        program.TryGetSymbol(fixture.WidgetClass, out var widgetSymbol).ShouldBeTrue();
        program.TryGetClassSymbol("__Packages.pkg.Widget", out var normalizedWidget)
            .ShouldBeTrue();
        normalizedWidget.ShouldBe(widgetSymbol);
        program[widgetSymbol].Kind.ShouldBe(Avm1SourceProgramSymbolKind.Class);
        program[widgetSymbol].QualifiedName.Value.ShouldBe("pkg.Widget");

        var peer = fixture.ConsumerClass.Fields.Single(field => field.Name == "peer");
        program.TryGetSymbol(peer, out var peerSymbol).ShouldBeTrue();
        var peerType = program[peerSymbol].DeclaredType;
        peerType.ShouldBe(program[peerSymbol].InferredType);
        program[peerType].Kind.ShouldBe(Avm1SourceTypeKind.Nominal);
        program[peerType].Name!.Value.ShouldBe("pkg.Widget");
        program[peerType].Symbol.ShouldBe(widgetSymbol);

        program.TryGetType("Number", out var numberType).ShouldBeTrue();
        numberType.ShouldBe(program.GetBuiltInType(Avm1SourceTypeKind.Number));
        program.ArenaBindings.Count.ShouldBe(6);
        program.ArenaBindings.Select(binding => binding.Arena).Distinct().Count()
            .ShouldBe(6);
    }

    [Fact]
    public void Binds_qualified_classes_static_members_and_current_instance_members()
    {
        var fixture = CreateProgramFixture();
        var program = fixture.Program;

        var consumerArena = fixture.ConsumerProbe.Body.Arena;
        var classReference = FindExpression(consumerArena, "pkg.Widget");
        var staticMember = FindExpression(consumerArena, "pkg.Widget.VALUE");
        var unknownMember = FindExpression(consumerArena, "unknown.value");

        var classBinding = program.GetBinding(consumerArena, classReference);
        classBinding.Kind.ShouldBe(Avm1SourceBindingKind.Bound);
        program[classBinding.Symbol].QualifiedName.Value.ShouldBe("pkg.Widget");

        var staticBinding = program.GetBinding(consumerArena, staticMember);
        staticBinding.Kind.ShouldBe(Avm1SourceBindingKind.Bound);
        program[staticBinding.Symbol].Kind.ShouldBe(Avm1SourceProgramSymbolKind.Field);
        program[staticBinding.Symbol].QualifiedName.Value.ShouldBe("pkg.Widget.VALUE");
        program.GetBinding(consumerArena, unknownMember).Kind
            .ShouldBe(Avm1SourceBindingKind.Dynamic);

        var widgetArena = fixture.WidgetProbe.Body.Arena;
        var instanceMember = FindExpression(widgetArena, "this.value");
        var instanceBinding = program.GetBinding(widgetArena, instanceMember);
        instanceBinding.Kind.ShouldBe(Avm1SourceBindingKind.Bound);
        program[instanceBinding.Symbol].QualifiedName.Value.ShouldBe("pkg.Widget.value");
    }

    [Fact]
    public void Disambiguates_getter_and_setter_bindings_from_expression_use()
    {
        var fixture = CreateProgramFixture();
        var program = fixture.Program;
        var arena = fixture.WidgetProbe.Body.Arena;
        program.TryGetBindings(arena, out var arenaBindings).ShouldBeTrue();

        var properties = FindExpressions(arena, "this.status");
        properties.Count.ShouldBe(2);
        var read = properties.Single(property =>
            arenaBindings!.GetUse(property) == Avm1SourceExpressionUse.Read);
        var write = properties.Single(property =>
            arenaBindings!.GetUse(property) == Avm1SourceExpressionUse.Write);

        var readBinding = arenaBindings![read];
        readBinding.Kind.ShouldBe(Avm1SourceBindingKind.Bound);
        program[readBinding.Symbol].Kind.ShouldBe(Avm1SourceProgramSymbolKind.Getter);
        var writeBinding = arenaBindings[write];
        writeBinding.Kind.ShouldBe(Avm1SourceBindingKind.Bound);
        program[writeBinding.Symbol].Kind.ShouldBe(Avm1SourceProgramSymbolKind.Setter);
    }

    [Fact]
    public void Retains_getter_and_setter_pair_for_read_write_use()
    {
        var method = BuildAccessorCompoundMethod();
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Widget"),
            baseType: null,
            interfaces: null,
            members:
            [
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
        var program = Avm1SourceProgram.Create([new Avm1SourceFile([sourceClass])]);
        program.TryGetBindings(method.Arena, out var arenaBindings).ShouldBeTrue();
        var property = FindExpression(method.Arena, "this.status");

        arenaBindings!.GetUse(property).ShouldBe(Avm1SourceExpressionUse.ReadWrite);
        var binding = arenaBindings[property];
        binding.Kind.ShouldBe(Avm1SourceBindingKind.Ambiguous);
        binding.Candidates.Count.ShouldBe(2);
        Enumerable.Range(0, binding.Candidates.Count)
            .Select(i => program[arenaBindings.GetCandidate(binding, i)].Kind)
            .OrderBy(kind => kind)
            .ShouldBe([
                Avm1SourceProgramSymbolKind.Getter,
                Avm1SourceProgramSymbolKind.Setter
            ]);
    }

    [Fact]
    public void Does_not_bind_package_looking_dynamic_names_through_with_scope()
    {
        var fixture = CreateProgramFixture();
        var arena = fixture.WithProbe.Body.Arena;
        var classReference = FindExpression(arena, "pkg.Widget");
        var staticMember = FindExpression(arena, "pkg.Widget.VALUE");

        fixture.Program.GetBinding(arena, classReference).Kind
            .ShouldBe(Avm1SourceBindingKind.Dynamic);
        fixture.Program.GetBinding(arena, staticMember).Kind
            .ShouldBe(Avm1SourceBindingKind.Dynamic);
    }

    [Fact]
    public void Does_not_bind_nested_function_this_to_the_containing_class()
    {
        var fixture = CreateProgramFixture();
        var arena = fixture.NestedThisProbe.Body.Arena;
        var nestedMember = FindExpression(arena, "this.value");

        fixture.Program.GetBinding(arena, nestedMember).Kind
            .ShouldBe(Avm1SourceBindingKind.Dynamic);
    }

    [Fact]
    public void Assigns_exact_code_unit_and_symbol_scopes_to_nested_functions()
    {
        var fixture = CreateProgramFixture();
        var arena = fixture.NestedThisProbe.Body.Arena;
        fixture.Program.TryGetBindings(arena, out var bindings).ShouldBeTrue();
        var scopes = bindings!.ScopeAnalysis;
        var functionExpression = arena.Expressions.Single(expression =>
            expression.Kind is Avm1SourceExpressionKind.FunctionLiteral);
        var function = arena[functionExpression.Function];
        var functionCodeUnit = scopes.GetCodeUnit(function.Index);
        var nestedMember = FindExpression(arena, "this.value");
        var nestedThis = arena[arena.GetChild(arena[nestedMember], 0)].Symbol;

        scopes.IsComplete.ShouldBeTrue();
        scopes.CodeUnits.Count.ShouldBe(2);
        functionCodeUnit.ShouldNotBe(scopes.RootCodeUnit);
        scopes[functionCodeUnit].Parent.ShouldBe(scopes.RootCodeUnit);
        scopes[functionCodeUnit].DeclarationScope
            .ShouldBe(scopes[scopes.RootCodeUnit].RootScope);
        scopes.GetCodeUnit(functionExpression.Index).ShouldBe(scopes.RootCodeUnit);
        scopes.GetCodeUnit(nestedMember).ShouldBe(functionCodeUnit);
        scopes.GetCodeUnit(nestedThis).ShouldBe(functionCodeUnit);
        scopes.IsRootCodeUnit(nestedMember).ShouldBeFalse();
    }

    [Fact]
    public void Assigns_a_distinct_dynamic_scope_to_with_body_only()
    {
        var fixture = CreateProgramFixture();
        var arena = fixture.WithProbe.Body.Arena;
        fixture.Program.TryGetBindings(arena, out var bindings).ShouldBeTrue();
        var scopes = bindings!.ScopeAnalysis;
        var scopeObject = FindExpression(arena, "scope");
        var classReference = FindExpression(arena, "pkg.Widget");
        var withScope = scopes.GetScope(classReference);

        scopes.IsComplete.ShouldBeTrue();
        scopes.Scopes.Count.ShouldBe(2);
        scopes.GetCodeUnit(scopeObject).ShouldBe(scopes.RootCodeUnit);
        scopes.IsInDynamicScope(scopeObject).ShouldBeFalse();
        scopes[withScope].Kind.ShouldBe(Avm1SourceScopeKind.With);
        scopes[withScope].Parent.ShouldBe(scopes[scopes.RootCodeUnit].RootScope);
        scopes.IsInDynamicScope(classReference).ShouldBeTrue();
    }

    [Fact]
    public void Restricts_a_catch_symbol_to_its_lexical_scope()
    {
        var tryBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("x"), PushValue.Integer(1)]),
            new ActionSetVariable()
        ], swfVersion: 6);
        var catchBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("caught")]),
            new ActionPush([PushValue.String("error")]),
            new ActionGetVariable(),
            new ActionSetVariable()
        ], swfVersion: 6);
        var method = new Avm1SourceMethodDeclaration(
            "probe",
            Avm1SourceMethodKind.Method,
            Avm1Decompiler.DecompileMethod([
                new ActionPush([
                    PushValue.String("error"),
                    PushValue.String("outer")]),
                new ActionDefineLocal(),
                new ActionTry(
                    TryFlags.CatchBlock,
                    catchRegister: 0,
                    catchVariable: "error",
                    tryBody,
                    catchBody,
                    ReadOnlyMemory<byte>.Empty),
                new ActionPush([PushValue.String("after")]),
                new ActionPush([PushValue.String("error")]),
                new ActionGetVariable(),
                new ActionSetVariable()
            ], swfVersion: 6).ProjectSource());
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var program = Avm1SourceProgram.Create([
            new Avm1SourceFile([sourceClass])
        ]);
        var arena = method.Body.Arena;
        program.TryGetBindings(arena, out var bindings).ShouldBeTrue();
        var scopes = bindings!.ScopeAnalysis;
        var catchClause = arena.Statements.Single(statement =>
            statement.Kind is Avm1SourceStatementKind.CatchClause);
        var catchSymbol = arena.Symbols.Single(symbol =>
            symbol.Kind is Avm1SourceSymbolKind.Catch);
        var outerSymbol = arena.Symbols.Single(symbol =>
            symbol.Kind is Avm1SourceSymbolKind.Local &&
            arena[symbol.Name] == "error");
        var catchReference = arena.Expressions.Single(expression =>
            expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
            expression.Symbol == catchSymbol.Index);
        var outerReference = arena.Expressions.Single(expression =>
            expression.Kind is Avm1SourceExpressionKind.SymbolReference &&
            expression.Symbol == outerSymbol.Index);
        var catchScope = scopes.GetScope(catchClause.Index);

        scopes.IsComplete.ShouldBeTrue();
        scopes[catchScope].Kind.ShouldBe(Avm1SourceScopeKind.Catch);
        scopes[catchScope].Parent.ShouldBe(scopes[scopes.RootCodeUnit].RootScope);
        scopes.GetScope(catchSymbol.Index).ShouldBe(catchScope);
        scopes.GetScope(outerSymbol.Index)
            .ShouldBe(scopes[scopes.RootCodeUnit].RootScope);
        scopes.IsSymbolVisibleAt(catchSymbol.Index, catchReference.Index)
            .ShouldBeTrue();
        scopes.IsSymbolVisibleAt(catchSymbol.Index, outerReference.Index)
            .ShouldBeFalse();
        scopes.IsSymbolVisibleAt(outerSymbol.Index, outerReference.Index)
            .ShouldBeTrue();
    }

    [Fact]
    public void Reports_cross_code_unit_source_node_reuse_as_incomplete()
    {
        var builder = new Avm1SourceArena.Builder();
        var shared = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1));
        var nestedReturn = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: shared);
        var nestedBody = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [nestedReturn]);
        var function = builder.AddFunction(
            SourceSymbolIndex.Invalid,
            nestedBody,
            Avm1SourceFunctionFlags.None,
            SourceOriginIndex.Invalid);
        var functionExpression = builder.AddExpression(
            Avm1SourceExpressionKind.FunctionLiteral,
            function: function);
        var functionStatement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: functionExpression);
        var rootReturn = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: shared);
        var root = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [functionStatement, rootReturn]);
        var method = new Avm1SourceMethod(builder.ToArena(), root, [], []);

        var scopes = Avm1SourceScopeAnalysis.Analyze(method);
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Invalid"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceMethodDeclaration(
                    "probe",
                    Avm1SourceMethodKind.Method,
                    method)
            ]);
        var program = Avm1SourceProgram.Create([
            new Avm1SourceFile([sourceClass])
        ]);

        scopes.IsComplete.ShouldBeFalse();
        scopes.ConflictCount.ShouldBeGreaterThan(0);
        scopes.GetScope(shared).IsValid.ShouldBeFalse();
        program.IsComplete.ShouldBeTrue();
        program.IsSemanticallyComplete.ShouldBeFalse();
    }

    [Fact]
    public void Does_not_bind_static_method_this_to_an_instance_member()
    {
        var method = new Avm1SourceMethodDeclaration(
            "probe",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("result")]),
                new ActionPush([PushValue.String("this")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("value")]),
                new ActionGetMember(),
                new ActionSetVariable()
            ]),
            Avm1SourceDeclarationModifiers.Static);
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Widget"),
            baseType: null,
            interfaces: null,
            members: [new Avm1SourceField("value"), method]);
        var program = Avm1SourceProgram.Create([
            new Avm1SourceFile([sourceClass])
        ]);
        var member = FindExpression(method.Body.Arena, "this.value");

        program.GetBinding(method.Body.Arena, member).Kind
            .ShouldBe(Avm1SourceBindingKind.Dynamic);
    }

    [Fact]
    public void Infers_proof_grade_nominal_local_and_method_return_types()
    {
        var method = new Avm1SourceMethodDeclaration(
            "create",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([
                    PushValue.String("item"),
                    PushValue.Integer(0),
                    PushValue.String("pkg.Widget")]),
                new ActionNewObject(),
                new ActionDefineLocal(),
                new ActionPush([PushValue.String("item")]),
                new ActionGetVariable(),
                new ActionReturn()
            ]));
        var forwardingMethod = new Avm1SourceMethodDeclaration(
            "forward",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.Integer(0)]),
                new ActionPush([PushValue.String("this")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("create")]),
                new ActionCallMethod(),
                new ActionReturn()
            ]));
        var widgetClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Widget"),
            baseType: null,
            interfaces: null,
            members: []);
        var consumerClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members: [method, forwardingMethod]);
        var widgetFile = new Avm1SourceFile([widgetClass]);
        var consumerFile = new Avm1SourceFile([consumerClass]);
        var program = Avm1SourceProgram.Create([widgetFile, consumerFile]);

        var analysis = program.TypeAnalysis;

        analysis.ReachedFixedPoint.ShouldBeTrue();
        analysis.IterationCount.ShouldBeGreaterThan(1);
        var item = method.Body.Arena.Symbols.Single(symbol =>
            method.Body.Arena[symbol.Name] == "item");
        var itemFact = analysis.GetSymbolFact(method.Body.Arena, item.Index);
        program[itemFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Nominal);
        program[itemFact.Type].Name!.Value.ShouldBe("pkg.Widget");
        itemFact.IsAnnotationSafe.ShouldBeTrue();
        itemFact.Provenance.HasFlag(Avm1SourceTypeProvenance.Constructor)
            .ShouldBeTrue();

        program.TryGetSymbol(method, out var methodSymbol).ShouldBeTrue();
        var returnFact = analysis[methodSymbol];
        returnFact.ShouldBe(analysis.GetMethodReturnFact(method.Body));
        returnFact.Type.ShouldBe(itemFact.Type);
        returnFact.IsAnnotationSafe.ShouldBeTrue();
        returnFact.Provenance.HasFlag(Avm1SourceTypeProvenance.Return)
            .ShouldBeTrue();
        analysis.TryGetAnnotation(returnFact, out var annotation).ShouldBeTrue();
        annotation!.Name.Value.ShouldBe("pkg.Widget");
        program.TryGetSymbol(forwardingMethod, out var forwardingSymbol).ShouldBeTrue();
        var forwardingFact = analysis[forwardingSymbol];
        forwardingFact.Type.ShouldBe(returnFact.Type);
        forwardingFact.IsAnnotationSafe.ShouldBeTrue();

        consumerFile.GetAs2Text().ShouldNotContain(": Widget");
        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(consumerFile).GetAs2Text();
        projected.ShouldContain("import pkg.Widget;");
        projected.ShouldContain("function create(): Widget");
        projected.ShouldContain("function forward(): Widget");
        projected.ShouldContain("var item: Widget = new Widget();");
    }

    [Fact]
    public void Conflicting_assignments_are_not_annotation_safe()
    {
        var method = new Avm1SourceMethodDeclaration(
            "mutate",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("value"), PushValue.Integer(1)]),
                new ActionDefineLocal(),
                new ActionPush([PushValue.String("value"), PushValue.String("changed")]),
                new ActionSetVariable()
            ]));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var program = Avm1SourceProgram.Create([sourceFile]);
        var analysis = program.TypeAnalysis;
        var value = method.Body.Arena.Symbols.Single(symbol =>
            method.Body.Arena[symbol.Name] == "value");

        var valueFact = analysis.GetSymbolFact(method.Body.Arena, value.Index);
        program[valueFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Conflict);
        valueFact.IsAnnotationSafe.ShouldBeFalse();
        analysis.TryGetAnnotation(valueFact, out _).ShouldBeFalse();
        program.TryGetSymbol(method, out var methodSymbol).ShouldBeTrue();
        var returnFact = analysis[methodSymbol];
        program[returnFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Void);
        returnFact.IsAnnotationSafe.ShouldBeTrue();

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("function mutate(): Void");
        projected.ShouldContain("var value = 1;");
        projected.ShouldNotContain("var value:");
    }

    [Fact]
    public void Joins_recovery_model_types_with_source_definitions()
    {
        var fieldBuilder = new Avm1SourceArena.Builder();
        var fieldValue = fieldBuilder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: fieldBuilder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: fieldBuilder.InternString("field")));
        var fieldInitializer = new Avm1SourceExpressionFragment(
            fieldBuilder.ToArena(),
            fieldValue,
            []);
        var field = new Avm1SourceField(
            "value",
            inferredType: new Avm1SourceTypeReference(
                new Avm1SourceQualifiedName("Number")),
            initializer: fieldInitializer);

        var methodBuilder = new Avm1SourceArena.Builder();
        var numberType = methodBuilder.GetBuiltInType(Avm1SourceTypeKind.Number);
        var local = methodBuilder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "local",
            SourceTypeIndex.Invalid,
            numberType);
        var localValue = methodBuilder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: methodBuilder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: methodBuilder.InternString("local")));
        var declaration = methodBuilder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: localValue,
            symbol: local);
        var body = methodBuilder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [declaration]);
        var method = new Avm1SourceMethodDeclaration(
            "test",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(methodBuilder.ToArena(), body, [], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.ModelConflict"),
            baseType: null,
            interfaces: null,
            members: [field, method]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var program = Avm1SourceProgram.Create([sourceFile]);

        program.TryGetSymbol(field, out var fieldSymbol).ShouldBeTrue();
        var fieldFact = program.TypeAnalysis[fieldSymbol];
        program[fieldFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Conflict);
        fieldFact.IsAnnotationSafe.ShouldBeFalse();
        fieldFact.Provenance.HasFlag(Avm1SourceTypeProvenance.ModelInference)
            .ShouldBeTrue();
        fieldFact.Provenance.HasFlag(Avm1SourceTypeProvenance.Literal)
            .ShouldBeTrue();
        var localFact = program.TypeAnalysis.GetSymbolFact(
            method.Body.Arena,
            local);
        program[localFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Conflict);
        localFact.IsAnnotationSafe.ShouldBeFalse();
        localFact.Provenance.HasFlag(Avm1SourceTypeProvenance.ModelInference)
            .ShouldBeTrue();

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("var value = \"field\";");
        projected.ShouldContain("var local = \"local\";");
        projected.ShouldNotContain("var value:");
        projected.ShouldNotContain("var local:");
    }

    [Fact]
    public void Infers_field_types_from_initializer_expressions()
    {
        var builder = new Avm1SourceArena.Builder();
        var literal = builder.AddLiteral(
            Avm1SourceLiteralKind.Boolean,
            booleanValue: true);
        var expression = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: literal);
        var initializer = new Avm1SourceExpressionFragment(
            builder.ToArena(),
            expression,
            []);
        var field = new Avm1SourceField("enabled", initializer: initializer);
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Settings"),
            baseType: null,
            interfaces: null,
            members: [field]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var program = Avm1SourceProgram.Create([sourceFile]);

        program.TryGetSymbol(field, out var fieldSymbol).ShouldBeTrue();
        var fieldFact = program.TypeAnalysis[fieldSymbol];
        program[fieldFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Boolean);
        fieldFact.IsAnnotationSafe.ShouldBeTrue();
        sourceFile.GetAs2Text().ShouldContain("var enabled = true;");
        program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text()
            .ShouldContain("var enabled: Boolean = true;");
    }

    [Fact]
    public void Emits_each_safe_multi_clause_for_variable_type()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var iSymbol = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "i",
            SourceTypeIndex.Invalid,
            unknown);
        var cSymbol = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "c",
            SourceTypeIndex.Invalid,
            unknown);
        var zero = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 0));
        var secondZero = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 0));
        var condition = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Boolean,
                booleanValue: true));
        var iReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: iSymbol);
        var cReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: cSymbol);
        var iUpdate = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [iReference]);
        var cUpdate = builder.AddExpression(
            Avm1SourceExpressionKind.Unary,
            Avm1SourceOperator.PostfixIncrement,
            children: [cReference]);
        var iDeclaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: zero,
            symbol: iSymbol);
        var cDeclaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: secondZero,
            symbol: cSymbol);
        var loopBody = builder.AddStatement(Avm1SourceStatementKind.Block);
        var loop = builder.AddStatement(
            Avm1SourceStatementKind.For,
            expression: condition,
            children: [loopBody],
            initializers: [iDeclaration, cDeclaration],
            expressions: [iUpdate, cUpdate]);
        var root = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [loop]);
        var method = new Avm1SourceMethodDeclaration(
            "count",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), root, [], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Counter"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var program = Avm1SourceProgram.Create([sourceFile]);

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();

        projected.ShouldContain(
            "for (var i: Number = 0, c: Number = 0; true; i++, c++)");
    }

    [Fact]
    public void Leaves_parameter_type_unspecified_when_only_coercing_uses_are_known()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var parameter = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            unknown,
            Avm1SourceSymbolFlags.DeclarationProvided);
        var parameterReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: parameter);
        var one = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1));
        var subtraction = builder.AddExpression(
            Avm1SourceExpressionKind.Binary,
            Avm1SourceOperator.Subtract,
            children: [parameterReference, one]);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: subtraction);
        var root = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [returnStatement]);
        var method = new Avm1SourceMethodDeclaration(
            "decrement",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), root, [parameter], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Counter"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var program = Avm1SourceProgram.Create([sourceFile]);

        var parameterFact = program.TypeAnalysis.GetSymbolFact(
            method.Body.Arena,
            parameter);
        program[parameterFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Unknown);
        parameterFact.Provenance.HasFlag(Avm1SourceTypeProvenance.Parameter)
            .ShouldBeTrue();
        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("function decrement(value): Number");
        projected.ShouldNotContain("value:");
    }

    [Fact]
    public void Binds_external_reference_symbols_and_propagates_exact_types()
    {
        static Avm1SourceTypeReference Type(string name) =>
            new(new Avm1SourceQualifiedName(name));

        var create = new Avm1SourceMethodDeclaration(
            "createExternal",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([
                    PushValue.String("item"),
                    PushValue.Integer(0),
                    PushValue.String("gfx.controls.Button")]),
                new ActionNewObject(),
                new ActionDefineLocal(),
                new ActionPush([PushValue.String("item")]),
                new ActionGetVariable(),
                new ActionReturn()
            ]));
        var defaultWidth = new Avm1SourceMethodDeclaration(
            "defaultWidth",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("gfx")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("controls")]),
                new ActionGetMember(),
                new ActionPush([PushValue.String("Button")]),
                new ActionGetMember(),
                new ActionPush([PushValue.String("DEFAULT_WIDTH")]),
                new ActionGetMember(),
                new ActionReturn()
            ]));
        var createViaFactory = new Avm1SourceMethodDeclaration(
            "createViaFactory",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.Integer(0)]),
                new ActionPush([PushValue.String("gfx")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("controls")]),
                new ActionGetMember(),
                new ActionPush([PushValue.String("Button")]),
                new ActionGetMember(),
                new ActionPush([PushValue.String("create")]),
                new ActionCallMethod(),
                new ActionReturn()
            ]));
        var newLabel = new Avm1SourceMethodDeclaration(
            "newLabel",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([
                    PushValue.Integer(0),
                    PushValue.String("gfx.controls.Button")]),
                new ActionNewObject(),
                new ActionPush([PushValue.String("label")]),
                new ActionGetMember(),
                new ActionReturn()
            ]));
        var overwriteWidth = new Avm1SourceMethodDeclaration(
            "overwriteWidth",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("gfx")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("controls")]),
                new ActionGetMember(),
                new ActionPush([PushValue.String("Button")]),
                new ActionGetMember(),
                new ActionPush([
                    PushValue.String("DEFAULT_WIDTH"),
                    PushValue.String("invalid")]),
                new ActionSetMember()
            ]));
        var consumerClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members:
            [
                create,
                defaultWidth,
                createViaFactory,
                newLabel,
                overwriteWidth
            ]);
        var sourceFile = new Avm1SourceFile([consumerClass]);
        var references = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("gfx.controls.Button"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "Button",
                        Avm1SourceReferenceMemberKind.Constructor,
                        parameters:
                        [
                            new Avm1SourceReferenceParameter(
                                "label",
                                Type("String"),
                                Avm1SourceReferenceParameterFlags.Optional)
                        ]),
                    new Avm1SourceReferenceMember(
                        "DEFAULT_WIDTH",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("Number"),
                        modifiers: Avm1SourceDeclarationModifiers.Public |
                            Avm1SourceDeclarationModifiers.Static),
                    new Avm1SourceReferenceMember(
                        "create",
                        Avm1SourceReferenceMemberKind.Method,
                        Type("gfx.controls.Button"),
                        modifiers: Avm1SourceDeclarationModifiers.Public |
                            Avm1SourceDeclarationModifiers.Static),
                    new Avm1SourceReferenceMember(
                        "label",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("String"))
                ],
                interfaces: [Type("gfx.core.IFocusable")]),
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("pkg.Consumer"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "externalOnly",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("Boolean"))
                ])
        ]);
        var program = Avm1SourceProgram.Create([sourceFile], references);

        program.TryGetClassSymbol("gfx.controls.Button", out var buttonSymbol)
            .ShouldBeTrue();
        program[buttonSymbol].Flags.HasFlag(Avm1SourceProgramSymbolFlags.External)
            .ShouldBeTrue();
        program.TryGetSignature(buttonSymbol, out var constructorSignature)
            .ShouldBeTrue();
        constructorSignature.Parameters.Count.ShouldBe(1);
        var labelParameter = program.GetParameter(constructorSignature, 0);
        labelParameter.Name.ShouldBe("label");
        program[labelParameter.Type].Kind.ShouldBe(Avm1SourceTypeKind.String);
        labelParameter.Flags.HasFlag(Avm1SourceProgramParameterFlags.Optional)
            .ShouldBeTrue();
        var interfaces = program.GetInterfaceTypes(buttonSymbol);
        interfaces.Count.ShouldBe(1);
        var interfaceType = program.GetInterfaceType(interfaces, 0);
        program[interfaceType].Name!.Value.ShouldBe("gfx.core.IFocusable");

        var factorySymbol = program.Symbols.Single(symbol =>
            symbol.QualifiedName.Value == "gfx.controls.Button.create");
        factorySymbol.Flags.HasFlag(Avm1SourceProgramSymbolFlags.External)
            .ShouldBeTrue();
        program.TryGetSignature(factorySymbol.Index, out var factorySignature)
            .ShouldBeTrue();
        factorySignature.Parameters.Count.ShouldBe(0);
        var factoryFact = program.TypeAnalysis[factorySymbol.Index];
        factoryFact.Provenance.HasFlag(Avm1SourceTypeProvenance.Reference)
            .ShouldBeTrue();
        var widthSymbol = program.Symbols.Single(symbol =>
            symbol.QualifiedName.Value == "gfx.controls.Button.DEFAULT_WIDTH");
        var widthFact = program.TypeAnalysis[widthSymbol.Index];
        program[widthFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Number);
        widthFact.Provenance.ShouldBe(Avm1SourceTypeProvenance.Reference);

        foreach (var method in new[] { create, createViaFactory })
        {
            var returnFact = program.TypeAnalysis.GetMethodReturnFact(method.Body);
            program[returnFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Nominal);
            program[returnFact.Type].Name!.Value.ShouldBe("gfx.controls.Button");
            returnFact.IsAnnotationSafe.ShouldBeTrue();
        }
        program[program.TypeAnalysis.GetMethodReturnFact(defaultWidth.Body).Type].Kind
            .ShouldBe(Avm1SourceTypeKind.Number);
        program[program.TypeAnalysis.GetMethodReturnFact(newLabel.Body).Type].Kind
            .ShouldBe(Avm1SourceTypeKind.String);

        program.TryGetSymbol(consumerClass, out var consumerSymbol).ShouldBeTrue();
        program[consumerSymbol].Flags.HasFlag(Avm1SourceProgramSymbolFlags.External)
            .ShouldBeFalse();
        program.Symbols.ShouldNotContain(symbol =>
            symbol.QualifiedName.Value == "pkg.Consumer.externalOnly");

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("import gfx.controls.Button;");
        projected.ShouldContain("function createExternal(): Button");
        projected.ShouldContain("var item: Button = new Button();");
        projected.ShouldContain("function defaultWidth(): Number");
        projected.ShouldContain("return Button.DEFAULT_WIDTH;");
        projected.ShouldContain("function createViaFactory(): Button");
        projected.ShouldContain("return Button.create();");
        projected.ShouldContain("function newLabel(): String");
    }

    [Fact]
    public void Binds_external_base_class_instance_members()
    {
        static Avm1SourceTypeReference Type(string name) =>
            new(new Avm1SourceQualifiedName(name));

        var method = new Avm1SourceMethodDeclaration(
            "isEnabled",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("this")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("enabled")]),
                new ActionGetMember(),
                new ActionReturn()
            ]));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            new Avm1SourceQualifiedName("gfx.controls.Widget"),
            interfaces: null,
            members:
            [
                new Avm1SourceField(
                    "enabled",
                    Avm1SourceDeclarationModifiers.Public |
                        Avm1SourceDeclarationModifiers.Static,
                    declaredType: Type("Number")),
                method
            ]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var references = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("gfx.controls.Widget"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "enabled",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("Boolean"))
                ])
        ]);
        var program = Avm1SourceProgram.Create([sourceFile], references);

        program.TryGetSymbol(sourceClass, out var sourceClassSymbol).ShouldBeTrue();
        program.TryGetClassSymbol("gfx.controls.Widget", out var baseClassSymbol)
            .ShouldBeTrue();
        program.TryGetBaseClass(sourceClassSymbol, out var resolvedBase).ShouldBeTrue();
        resolvedBase.ShouldBe(baseClassSymbol);
        program.TryGetBaseType(sourceClassSymbol, out var resolvedBaseType)
            .ShouldBeTrue();
        program[resolvedBaseType].Name!.Value.ShouldBe("gfx.controls.Widget");
        var member = FindExpression(method.Body.Arena, "this.enabled");
        var binding = program.GetBinding(method.Body.Arena, member);
        binding.IsBound.ShouldBeTrue();
        program[binding.Symbol].Flags.HasFlag(Avm1SourceProgramSymbolFlags.External)
            .ShouldBeTrue();
        program[binding.Symbol].Kind.ShouldBe(Avm1SourceProgramSymbolKind.Field);
        var returnFact = program.TypeAnalysis.GetMethodReturnFact(method.Body);
        program[returnFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Boolean);
        returnFact.Provenance.HasFlag(Avm1SourceTypeProvenance.Reference)
            .ShouldBeTrue();

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("import gfx.controls.Widget;");
        projected.ShouldContain("function isEnabled(): Boolean");
    }

    [Fact]
    public void Binds_declared_nominal_instance_receivers_from_source_hir()
    {
        static Avm1SourceTypeReference Type(string name) =>
            new(new Avm1SourceQualifiedName(name));

        var builder = new Avm1SourceArena.Builder();
        var buttonType = builder.GetNominalType("gfx.controls.Button");
        var parameter = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "button",
            buttonType,
            buttonType,
            Avm1SourceSymbolFlags.DeclarationProvided);
        var receiver = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: parameter);
        var memberName = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("label")));
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children: [receiver, memberName]);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: member);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [returnStatement]);
        var method = new Avm1SourceMethodDeclaration(
            "getLabel",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), body, [parameter], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var references = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("gfx.controls.Button"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "label",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("String"))
                ])
        ]);
        var program = Avm1SourceProgram.Create([sourceFile], references);

        var binding = program.GetBinding(method.Body.Arena, member);
        binding.IsBound.ShouldBeTrue();
        program[binding.Symbol].Flags.HasFlag(Avm1SourceProgramSymbolFlags.External)
            .ShouldBeTrue();
        program.TryGetSymbol(method, out var methodSymbol).ShouldBeTrue();
        program.TryGetSignature(methodSymbol, out var signature).ShouldBeTrue();
        var signatureParameter = program.GetParameter(signature, 0);
        program[signatureParameter.Type].Name!.Value.ShouldBe("gfx.controls.Button");
        var returnFact = program.TypeAnalysis.GetMethodReturnFact(method.Body);
        program[returnFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.String);

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("import gfx.controls.Button;");
        projected.ShouldContain("function getLabel(button: Button): String");
        projected.ShouldContain("return button.label;");
    }

    [Fact]
    public void Refines_inferred_nominal_receiver_bindings_to_a_semantic_fixed_point()
    {
        static Avm1SourceTypeReference Type(string name) =>
            new(new Avm1SourceQualifiedName(name));

        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var button = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "button",
            SourceTypeIndex.Invalid,
            unknown);
        var skin = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "skin",
            SourceTypeIndex.Invalid,
            unknown);
        var buttonClass = builder.AddExpression(
            Avm1SourceExpressionKind.QualifiedName,
            name: builder.InternString("gfx.controls.Button"));
        var newButton = builder.AddExpression(
            Avm1SourceExpressionKind.New,
            children: [buttonClass]);
        var buttonDeclaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: newButton,
            symbol: button);
        var buttonReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: button);
        var skinName = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("skin")));
        var skinMember = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children: [buttonReference, skinName]);
        var skinDeclaration = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: skinMember,
            symbol: skin);
        var skinReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: skin);
        var labelName = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("label")));
        var labelMember = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children: [skinReference, labelName]);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: labelMember);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [buttonDeclaration, skinDeclaration, returnStatement]);
        var method = new Avm1SourceMethodDeclaration(
            "getLabel",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), body, [], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var references = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("gfx.controls.Button"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "skin",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("gfx.controls.ButtonSkin"))
                ]),
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("gfx.controls.ButtonSkin"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "label",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("String"))
                ])
        ]);
        var program = Avm1SourceProgram.Create([sourceFile], references);

        program.SemanticAnalysis.ReachedFixedPoint.ShouldBeTrue();
        program.SemanticAnalysis.IterationCount.ShouldBe(3);
        program.SemanticAnalysis.RefinedBindingCount.ShouldBe(2);
        program.GetBinding(method.Body.Arena, skinMember).IsBound.ShouldBeTrue();
        program.GetBinding(method.Body.Arena, labelMember).IsBound.ShouldBeTrue();
        program[program.TypeAnalysis.GetSymbolFact(method.Body.Arena, button).Type]
            .Name!.Value.ShouldBe("gfx.controls.Button");
        program[program.TypeAnalysis.GetSymbolFact(method.Body.Arena, skin).Type]
            .Name!.Value.ShouldBe("gfx.controls.ButtonSkin");
        program[program.TypeAnalysis.GetMethodReturnFact(method.Body).Type].Kind
            .ShouldBe(Avm1SourceTypeKind.String);

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("var button: Button = new Button();");
        projected.ShouldContain("var skin: ButtonSkin = button.skin;");
        projected.ShouldContain("function getLabel(): String");
    }

    [Fact]
    public void Does_not_refine_a_class_reference_to_an_instance_member()
    {
        static Avm1SourceTypeReference Type(string name) =>
            new(new Avm1SourceQualifiedName(name));

        var builder = new Avm1SourceArena.Builder();
        var buttonClass = builder.AddExpression(
            Avm1SourceExpressionKind.QualifiedName,
            name: builder.InternString("gfx.controls.Button"));
        var labelName = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("label")));
        var labelMember = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children: [buttonClass, labelName]);
        var returnStatement = builder.AddStatement(
            Avm1SourceStatementKind.Return,
            expression: labelMember);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [returnStatement]);
        var method = new Avm1SourceMethodDeclaration(
            "invalidStaticAccess",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), body, [], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var references = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("gfx.controls.Button"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "label",
                        Avm1SourceReferenceMemberKind.Field,
                        Type("String"))
                ])
        ]);
        var program = Avm1SourceProgram.Create(
            [new Avm1SourceFile([sourceClass])],
            references);

        program.SemanticAnalysis.ReachedFixedPoint.ShouldBeTrue();
        program.SemanticAnalysis.RefinedBindingCount.ShouldBe(0);
        program.GetBinding(method.Body.Arena, labelMember).Kind
            .ShouldBe(Avm1SourceBindingKind.Dynamic);
    }

    [Fact]
    public void Retains_external_parameter_contract_without_typing_the_caller_parameter()
    {
        static Avm1SourceTypeReference Type(string name) =>
            new(new Avm1SourceQualifiedName(name));

        var builder = new Avm1SourceArena.Builder();
        var unknown = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var parameter = builder.AddSymbol(
            Avm1SourceSymbolKind.Parameter,
            "value",
            SourceTypeIndex.Invalid,
            unknown,
            Avm1SourceSymbolFlags.DeclarationProvided);
        var result = builder.AddSymbol(
            Avm1SourceSymbolKind.Local,
            "result",
            SourceTypeIndex.Invalid,
            unknown);
        var classReference = builder.AddExpression(
            Avm1SourceExpressionKind.QualifiedName,
            name: builder.InternString("gfx.Api"));
        var memberName = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.String,
                stringValue: builder.InternString("consume")));
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children: [classReference, memberName]);
        var argument = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: parameter);
        var call = builder.AddExpression(
            Avm1SourceExpressionKind.Call,
            children: [member, argument]);
        var callStatement = builder.AddStatement(
            Avm1SourceStatementKind.VariableDeclaration,
            expression: call,
            symbol: result);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [callStatement]);
        var method = new Avm1SourceMethodDeclaration(
            "send",
            Avm1SourceMethodKind.Method,
            new Avm1SourceMethod(builder.ToArena(), body, [parameter], []));
        var sourceClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members: [method]);
        var sourceFile = new Avm1SourceFile([sourceClass]);
        var references = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("gfx.Api"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "consume",
                        Avm1SourceReferenceMemberKind.Method,
                        Type("Void"),
                        parameters:
                        [
                            new Avm1SourceReferenceParameter(
                                "value",
                                Type("Number"))
                        ],
                        modifiers: Avm1SourceDeclarationModifiers.Public |
                            Avm1SourceDeclarationModifiers.Static)
                ])
        ]);
        var program = Avm1SourceProgram.Create([sourceFile], references);

        var consume = program.Symbols.Single(symbol =>
            symbol.QualifiedName.Value == "gfx.Api.consume");
        program.TryGetSignature(consume.Index, out var signature).ShouldBeTrue();
        signature.Parameters.Count.ShouldBe(1);
        program[program.GetParameter(signature, 0).Type].Kind
            .ShouldBe(Avm1SourceTypeKind.Number);
        var parameterFact = program.TypeAnalysis.GetSymbolFact(
            method.Body.Arena,
            parameter);
        program[parameterFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Unknown);
        parameterFact.Provenance.HasFlag(Avm1SourceTypeProvenance.Parameter)
            .ShouldBeTrue();
        var resultFact = program.TypeAnalysis.GetSymbolFact(
            method.Body.Arena,
            result);
        program[resultFact.Type].Kind.ShouldBe(Avm1SourceTypeKind.Void);

        var projected = program.CreateProjection(new Avm1SourceProjectionOptions
        {
            EmitInferredTypes = true
        }).GetFile(sourceFile).GetAs2Text();
        projected.ShouldContain("function send(value): Void");
        projected.ShouldContain("var result = Api.consume(value);");
        projected.ShouldNotContain("value:");
        projected.ShouldNotContain("result:");
    }

    private static ProgramFixture CreateProgramFixture()
    {
        var widgetProbe = new Avm1SourceMethodDeclaration(
            "probe",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("result")]),
                new ActionPush([PushValue.String("this")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("value")]),
                new ActionGetMember(),
                new ActionSetVariable(),
                new ActionPush([PushValue.String("result")]),
                new ActionPush([PushValue.String("this")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("status")]),
                new ActionGetMember(),
                new ActionSetVariable(),
                new ActionPush([PushValue.String("this")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("status"), PushValue.Boolean(true)]),
                new ActionSetMember()
            ]));
        var widgetClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Widget"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceField("value"),
                new Avm1SourceField(
                    "VALUE",
                    Avm1SourceDeclarationModifiers.Static,
                    declaredType: new Avm1SourceTypeReference(
                        new Avm1SourceQualifiedName("Number"))),
                new Avm1SourceMethodDeclaration(
                    "status",
                    Avm1SourceMethodKind.Getter,
                    Project([]),
                    origin: new Avm1SourceMemberOrigin("__get__status")),
                new Avm1SourceMethodDeclaration(
                    "status",
                    Avm1SourceMethodKind.Setter,
                    Project([]),
                    origin: new Avm1SourceMemberOrigin("__set__status")),
                CreateNestedThisProbe(),
                widgetProbe
            ]);

        var consumerProbe = new Avm1SourceMethodDeclaration(
            "probe",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("result")]),
                new ActionPush([PushValue.String("pkg")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("Widget")]),
                new ActionGetMember(),
                new ActionPush([PushValue.String("VALUE")]),
                new ActionGetMember(),
                new ActionSetVariable(),
                new ActionPush([PushValue.String("result")]),
                new ActionPush([PushValue.String("unknown")]),
                new ActionGetVariable(),
                new ActionPush([PushValue.String("value")]),
                new ActionGetMember(),
                new ActionSetVariable()
            ]));
        var withBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("result")]),
            new ActionPush([PushValue.String("pkg")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("Widget")]),
            new ActionGetMember(),
            new ActionPush([PushValue.String("VALUE")]),
            new ActionGetMember(),
            new ActionSetVariable()
        ], swfVersion: 7);
        var withProbe = new Avm1SourceMethodDeclaration(
            "withProbe",
            Avm1SourceMethodKind.Method,
            Project([
                new ActionPush([PushValue.String("scope")]),
                new ActionGetVariable(),
                new ActionWith(withBody)
            ]));
        var widgetType = new Avm1SourceTypeReference(
            new Avm1SourceQualifiedName("pkg.Widget"));
        var consumerClass = new Avm1SourceClass(
            new Avm1SourceQualifiedName("pkg.Consumer"),
            baseType: null,
            interfaces: null,
            members:
            [
                new Avm1SourceField(
                    "peer",
                    declaredType: widgetType,
                    inferredType: widgetType),
                consumerProbe,
                withProbe
            ]);

        var nestedThisProbe = widgetClass.Methods.Single(method =>
            method.Name == "nestedThisProbe");
        var program = Avm1SourceProgram.Create([
            new Avm1SourceFile([widgetClass]),
            new Avm1SourceFile([consumerClass])
        ]);
        return new ProgramFixture(
            program,
            widgetClass,
            consumerClass,
            widgetProbe,
            consumerProbe,
            withProbe,
            nestedThisProbe);
    }

    private static Avm1SourceMethodDeclaration CreateNestedThisProbe()
    {
        var functionBody = Avm1Action.EncodeCollection([
            new ActionPush([PushValue.String("this")]),
            new ActionGetVariable(),
            new ActionPush([PushValue.String("value")]),
            new ActionGetMember(),
            new ActionReturn()
        ], swfVersion: 7);
        return new Avm1SourceMethodDeclaration(
            "nestedThisProbe",
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

    private static Avm1SourceMethod Project(IReadOnlyList<Avm1Action> actions) =>
        Avm1Decompiler.DecompileMethod(actions, swfVersion: 7).ProjectSource();

    private static Avm1SourceMethod BuildAccessorCompoundMethod()
    {
        var builder = new Avm1SourceArena.Builder();
        var unknownType = builder.GetBuiltInType(Avm1SourceTypeKind.Unknown);
        var thisSymbol = builder.AddSymbol(
            Avm1SourceSymbolKind.This,
            "this",
            SourceTypeIndex.Invalid,
            unknownType,
            Avm1SourceSymbolFlags.DeclarationProvided);
        var thisReference = builder.AddExpression(
            Avm1SourceExpressionKind.SymbolReference,
            symbol: thisSymbol);
        var statusName = builder.AddLiteral(
            Avm1SourceLiteralKind.String,
            stringValue: builder.InternString("status"));
        var statusNameExpression = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: statusName);
        var member = builder.AddExpression(
            Avm1SourceExpressionKind.MemberAccess,
            children: [thisReference, statusNameExpression]);
        var one = builder.AddExpression(
            Avm1SourceExpressionKind.Literal,
            literal: builder.AddLiteral(
                Avm1SourceLiteralKind.Integer,
                integerValue: 1));
        var assignment = builder.AddExpression(
            Avm1SourceExpressionKind.Assignment,
            Avm1SourceOperator.AddAssign,
            children: [member, one]);
        var statement = builder.AddStatement(
            Avm1SourceStatementKind.Expression,
            expression: assignment);
        var body = builder.AddStatement(
            Avm1SourceStatementKind.Block,
            children: [statement]);
        return new Avm1SourceMethod(builder.ToArena(), body, [], []);
    }

    private static SourceExpressionIndex FindExpression(
        Avm1SourceArena arena,
        string text)
    {
        return FindExpressions(arena, text).Single();
    }

    private static IReadOnlyList<SourceExpressionIndex> FindExpressions(
        Avm1SourceArena arena,
        string text)
    {
        return arena.Expressions.Where(expression =>
            new Avm1SourceExpressionFragment(arena, expression.Index, [])
                .GetAs2Text() == text).Select(expression => expression.Index).ToArray();
    }

    private sealed record ProgramFixture(
        Avm1SourceProgram Program,
        Avm1SourceClass WidgetClass,
        Avm1SourceClass ConsumerClass,
        Avm1SourceMethodDeclaration WidgetProbe,
        Avm1SourceMethodDeclaration ConsumerProbe,
        Avm1SourceMethodDeclaration WithProbe,
        Avm1SourceMethodDeclaration NestedThisProbe);
}
