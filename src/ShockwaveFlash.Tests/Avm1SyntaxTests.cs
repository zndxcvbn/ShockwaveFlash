using ShockwaveFlash.Avm1.Compilation.Syntax;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SyntaxTests
{
    private static readonly string[] SkyUiSourceRoots =
    [
        @"C:\Users\artem\source\repos\skyui\src\CraftingMenu",
        @"C:\Users\artem\source\repos\skyui\src\Common",
        @"C:\Users\artem\source\repos\skyui\src\CLIK"
    ];

    [Fact]
    public void Lexer_preserves_every_source_character_as_token_or_trivia()
    {
        const string source =
            "// heading\r\n" +
            "dynamic class test.Sample {\n" +
            "  var value = .5e+2 >>> 1; /* tail */\n" +
            "  function run(a:String = \"x\\\"y\"):Void { value += a.length; }\n" +
            "}\n";

        var tokens = Avm1Lexer.Lex(source);

        tokens.HasErrors.ShouldBeFalse(Describe(tokens.Diagnostics, source));
        tokens.GetTokenizedText().ShouldBe(source);
        tokens.Tokens[^1].Kind.ShouldBe(Avm1SyntaxKind.EndOfFileToken);
        tokens.Tokens.ShouldContain(token =>
            token.Kind == Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanToken);
        tokens.Trivia.ShouldContain(trivia =>
            trivia.Kind == Avm1SyntaxTriviaKind.SingleLineComment);
        tokens.Trivia.ShouldContain(trivia =>
            trivia.Kind == Avm1SyntaxTriviaKind.MultiLineComment);
    }

    [Fact]
    public void Lexer_reports_malformed_input_without_losing_text()
    {
        const string source = "var hex = 0x; var text = 'unterminated\n/* open";

        var tokens = Avm1Lexer.Lex(source);

        tokens.GetTokenizedText().ShouldBe(source);
        tokens.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldBe([
            "AVM1S0003",
            "AVM1S0004",
            "AVM1S0002"
        ]);
    }

    [Theory]
    [InlineData("++", Avm1SyntaxKind.PlusPlusToken)]
    [InlineData("+=", Avm1SyntaxKind.PlusEqualsToken)]
    [InlineData("--", Avm1SyntaxKind.MinusMinusToken)]
    [InlineData("-=", Avm1SyntaxKind.MinusEqualsToken)]
    [InlineData("*=", Avm1SyntaxKind.AsteriskEqualsToken)]
    [InlineData("/=", Avm1SyntaxKind.SlashEqualsToken)]
    [InlineData("%=", Avm1SyntaxKind.PercentEqualsToken)]
    [InlineData("===", Avm1SyntaxKind.EqualsEqualsEqualsToken)]
    [InlineData("!==", Avm1SyntaxKind.ExclamationEqualsEqualsToken)]
    [InlineData("<<=", Avm1SyntaxKind.LessThanLessThanEqualsToken)]
    [InlineData(">>=", Avm1SyntaxKind.GreaterThanGreaterThanEqualsToken)]
    [InlineData(">>>=", Avm1SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken)]
    [InlineData("&&", Avm1SyntaxKind.AmpersandAmpersandToken)]
    [InlineData("||", Avm1SyntaxKind.PipePipeToken)]
    [InlineData("&=", Avm1SyntaxKind.AmpersandEqualsToken)]
    [InlineData("|=", Avm1SyntaxKind.PipeEqualsToken)]
    [InlineData("^=", Avm1SyntaxKind.CaretEqualsToken)]
    [InlineData("::", Avm1SyntaxKind.DoubleColonToken)]
    public void Lexer_uses_maximal_munch_for_compound_operators(
        string source,
        Avm1SyntaxKind expected)
    {
        var tokens = Avm1Lexer.Lex(source);

        tokens.HasErrors.ShouldBeFalse(Describe(tokens.Diagnostics, source));
        tokens.Tokens.Select(token => token.Kind).ShouldBe([
            expected,
            Avm1SyntaxKind.EndOfFileToken
        ]);
        tokens.GetTokenizedText().ShouldBe(source);
    }

    [Fact]
    public void Parser_recovers_qualified_type_and_member_declarations()
    {
        const string source = """
            import gfx.controls.Button;
            import skyui.components.list.*;

            dynamic class sample.widgets.Example extends MovieClip implements IOne, sample.ITwo
            {
                #include "../version.as"
                private static var count:Number = 1, values:Array = [1, {x:2, y:3}];

                function Example(initial:Number = choose(1, 2))
                {
                    count = initial;
                }

                public function get current():Number { return count; }
                public function set current(value:Number):Void { count = value; }
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        tree.TokenStream.GetTokenizedText().ShouldBe(source);
        tree.Imports.Count.ShouldBe(2);
        tree.GetNameText(tree.Imports[0].Name).ToString()
            .ShouldBe("gfx.controls.Button");
        tree.GetNameText(tree.Imports[1].Name).ToString()
            .ShouldBe("skyui.components.list.*");

        var type = tree.Types.ShouldHaveSingleItem();
        type.Kind.ShouldBe(Avm1TypeDeclarationKind.Class);
        type.Modifiers.ShouldBe(Avm1SyntaxModifiers.Dynamic);
        tree.GetNameText(type.Name).ToString().ShouldBe("sample.widgets.Example");
        tree.GetBaseTypes(type).ToArray()
            .Select(name => tree.GetNameText(name).ToString())
            .ShouldBe(["MovieClip"]);
        tree.GetImplementedTypes(type).ToArray()
            .Select(name => tree.GetNameText(name).ToString())
            .ShouldBe(["IOne", "sample.ITwo"]);

        var members = tree.GetMembers(type).ToArray();
        members.Select(member => member.Kind).ShouldBe([
            Avm1MemberDeclarationKind.Directive,
            Avm1MemberDeclarationKind.Field,
            Avm1MemberDeclarationKind.Constructor,
            Avm1MemberDeclarationKind.Getter,
            Avm1MemberDeclarationKind.Setter
        ]);
        var directive = tree.GetDirective(members[0].Directive);
        tree.GetTokenText(directive.NameToken).ToString().ShouldBe("include");
        tree.GetText(directive.ArgumentSpan).ToString().ShouldBe("\"../version.as\"");

        var fields = tree.GetDeclarators(members[1]).ToArray();
        fields.Length.ShouldBe(2);
        tree.GetTokenText(fields[0].NameToken).ToString().ShouldBe("count");
        tree.GetNameText(fields[0].DeclaredType).ToString().ShouldBe("Number");
        tree.GetText(fields[1].InitializerSpan).ToString()
            .ShouldBe("[1, {x:2, y:3}]");

        var constructorParameter = tree.GetParameters(members[2])
            .ToArray()
            .ShouldHaveSingleItem();
        tree.GetTokenText(constructorParameter.NameToken).ToString()
            .ShouldBe("initial");
        tree.GetText(constructorParameter.DefaultValueSpan).ToString()
            .ShouldBe("choose(1, 2)");
        members[2].BodySpan.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Parser_recovers_interface_signatures_without_method_bodies()
    {
        const string source = """
            interface sample.IList extends sample.IBase, IOther
            {
                public function size():Number;
                function at(index:Number):Object;
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var type = tree.Types.ShouldHaveSingleItem();
        type.Kind.ShouldBe(Avm1TypeDeclarationKind.Interface);
        tree.GetBaseTypes(type).Length.ShouldBe(2);
        var members = tree.GetMembers(type).ToArray();
        members.Length.ShouldBe(2);
        members.ShouldAllBe(member =>
            member.Kind == Avm1MemberDeclarationKind.Method &&
            !member.BodySpan.IsValid);
    }

    [Fact]
    public void Expression_parser_preserves_precedence_and_assignment_associativity()
    {
        const string source =
            "class Test { var value = first = second = a + b * c; }";

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var root = GetOnlyInitializer(tree);
        var firstAssignment = tree.GetExpression(root);
        firstAssignment.Kind.ShouldBe(Avm1ExpressionSyntaxKind.AssignmentExpression);
        tree.GetTokenText(firstAssignment.OperatorToken).ToString().ShouldBe("=");

        var firstChildren = tree.GetChildren(firstAssignment).ToArray();
        GetTokenText(tree, firstChildren[0]).ShouldBe("first");
        var secondAssignment = tree.GetExpression(firstChildren[1]);
        secondAssignment.Kind.ShouldBe(Avm1ExpressionSyntaxKind.AssignmentExpression);
        var secondChildren = tree.GetChildren(secondAssignment).ToArray();
        GetTokenText(tree, secondChildren[0]).ShouldBe("second");

        var addition = tree.GetExpression(secondChildren[1]);
        addition.Kind.ShouldBe(Avm1ExpressionSyntaxKind.BinaryExpression);
        tree.GetTokenText(addition.OperatorToken).ToString().ShouldBe("+");
        var additionChildren = tree.GetChildren(addition).ToArray();
        GetTokenText(tree, additionChildren[0]).ShouldBe("a");
        var multiplication = tree.GetExpression(additionChildren[1]);
        multiplication.Kind.ShouldBe(Avm1ExpressionSyntaxKind.BinaryExpression);
        tree.GetTokenText(multiplication.OperatorToken).ToString().ShouldBe("*");
    }

    [Fact]
    public void Expression_parser_recovers_postfix_new_array_and_object_shapes()
    {
        const string source = """
            class Test
            {
                var value = new pkg.Builder(factory()[index++],
                    {name:"x", flags:[1,,3]}).result;
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var root = tree.GetExpression(GetOnlyInitializer(tree));
        root.Kind.ShouldBe(Avm1ExpressionSyntaxKind.MemberAccessExpression);
        tree.GetTokenText(root.Token).ToString().ShouldBe("result");
        var construction = tree.GetExpression(tree.GetChildren(root)[0]);
        construction.Kind.ShouldBe(Avm1ExpressionSyntaxKind.NewExpression);
        var kinds = DescendantsAndSelf(tree, construction.Index)
            .Select(expression => expression.Kind)
            .ToArray();
        kinds.ShouldContain(Avm1ExpressionSyntaxKind.InvocationExpression);
        kinds.ShouldContain(Avm1ExpressionSyntaxKind.ElementAccessExpression);
        kinds.ShouldContain(Avm1ExpressionSyntaxKind.PostfixUnaryExpression);
        kinds.ShouldContain(Avm1ExpressionSyntaxKind.ObjectLiteralExpression);
        kinds.ShouldContain(Avm1ExpressionSyntaxKind.ObjectProperty);
        kinds.ShouldContain(Avm1ExpressionSyntaxKind.ArrayLiteralExpression);
        kinds.ShouldContain(Avm1ExpressionSyntaxKind.Omitted);
    }

    [Fact]
    public void Expression_parser_retains_function_expression_envelope()
    {
        const string source = """
            class Test
            {
                var callback = function named(value:Number):Number {
                    return value + 1;
                };
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var function = tree.GetExpression(GetOnlyInitializer(tree));
        function.Kind.ShouldBe(Avm1ExpressionSyntaxKind.FunctionExpression);
        tree.GetTokenText(function.OperatorToken).ToString().ShouldBe("function");
        tree.GetTokenText(function.Token).ToString().ShouldBe("named");
        function.AuxiliaryTokens.Count.ShouldBe(3);
        tree.GetParameters(function).Length.ShouldBe(1);
        tree.GetNameText(function.DeclaredType).ToString().ShouldBe("Number");
        tree.GetText(function.BodySpan).ToString().ShouldContain("return value + 1;");
        function.Body.IsValid.ShouldBeTrue();
        var body = tree.GetStatement(function.Body);
        body.Kind.ShouldBe(Avm1StatementSyntaxKind.Block);
        body.Span.ShouldBe(function.BodySpan);
    }

    [Fact]
    public void Expression_parser_recovers_a_missing_operand_without_consuming_the_field_boundary()
    {
        const string source =
            "class Test { var broken = left + ; var good = 2; }";

        var tree = Avm1SyntaxTree.Parse(source);

        tree.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1S2001");
        var members = tree.GetMembers(tree.Types.ShouldHaveSingleItem()).ToArray();
        members.Length.ShouldBe(2);
        var good = tree.GetDeclarators(members[1]).ToArray().ShouldHaveSingleItem();
        tree.GetTokenText(good.NameToken).ToString().ShouldBe("good");
        tree.GetExpression(good.Initializer).Kind
            .ShouldBe(Avm1ExpressionSyntaxKind.LiteralExpression);
    }

    [Fact]
    public void Statement_parser_recovers_loops_labels_and_ordered_for_clauses()
    {
        const string source = """
            class Test
            {
                function run(items:Object):Number
                {
                    var total:Number = 0, i:Number = 0;
                    outer: for (i = 0; i < items.length; i++, total += i)
                    {
                        if (items[i] == null)
                            continue outer;
                        while (total < 10)
                        {
                            total++;
                            break;
                        }
                    }
                    do total--; while (total > 5);
                    for (var key:String in items)
                        total += items[key];
                    return total;
                }
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var method = GetOnlyMethod(tree);
        var statements = StatementDescendantsAndSelf(tree, method.Body).ToArray();
        var kinds = statements.Select(statement => statement.Kind).ToArray();
        kinds.ShouldContain(Avm1StatementSyntaxKind.VariableDeclaration);
        kinds.ShouldContain(Avm1StatementSyntaxKind.Labeled);
        kinds.ShouldContain(Avm1StatementSyntaxKind.For);
        kinds.ShouldContain(Avm1StatementSyntaxKind.If);
        kinds.ShouldContain(Avm1StatementSyntaxKind.Continue);
        kinds.ShouldContain(Avm1StatementSyntaxKind.While);
        kinds.ShouldContain(Avm1StatementSyntaxKind.Break);
        kinds.ShouldContain(Avm1StatementSyntaxKind.DoWhile);
        kinds.ShouldContain(Avm1StatementSyntaxKind.ForIn);
        kinds.ShouldContain(Avm1StatementSyntaxKind.Return);

        var forStatement = statements.Single(statement =>
            statement.Kind == Avm1StatementSyntaxKind.For);
        tree.GetInitializers(forStatement).Length.ShouldBe(1);
        tree.GetExpressions(forStatement).Length.ShouldBe(2);
        forStatement.Expression.IsValid.ShouldBeTrue();
        var forIn = statements.Single(statement =>
            statement.Kind == Avm1StatementSyntaxKind.ForIn);
        tree.GetDeclarators(forIn).Length.ShouldBe(1);
        forIn.Expression.IsValid.ShouldBeTrue();
        forIn.SecondaryExpression.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Statement_parser_preserves_switch_fallthrough_and_nested_functions()
    {
        const string source = """
            class Test
            {
                function choose(value:Number):Object
                {
                    function normalize(input:Number):Number
                    {
                        return input;
                    }
                    switch (value)
                    {
                        case 1:
                        case 2:
                            return normalize(value);
                        default:
                            return null;
                    }
                }
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var statements = StatementDescendantsAndSelf(
            tree,
            GetOnlyMethod(tree).Body).ToArray();
        var nestedFunction = statements.Single(statement =>
            statement.Kind == Avm1StatementSyntaxKind.FunctionDeclaration);
        tree.GetTokenText(nestedFunction.NameToken).ToString()
            .ShouldBe("normalize");
        tree.GetParameters(nestedFunction).Length.ShouldBe(1);
        tree.GetChildren(nestedFunction).Length.ShouldBe(1);

        var switchStatement = statements.Single(statement =>
            statement.Kind == Avm1StatementSyntaxKind.Switch);
        var sections = tree.GetChildren(switchStatement)
            .ToArray()
            .Select(tree.GetStatement)
            .ToArray();
        sections.Length.ShouldBe(3);
        tree.GetChildren(sections[0]).Length.ShouldBe(0);
        sections[2].Flags.ShouldBe(Avm1StatementSyntaxFlags.HasDefaultLabel);
    }

    [Fact]
    public void Statement_parser_models_try_with_throw_catch_and_finally()
    {
        const string source = """
            class Test
            {
                function run(scope:Object):Object
                {
                    try
                    {
                        with (scope)
                        {
                            if (error)
                                throw error;
                        }
                    }
                    catch (failure:Error)
                    {
                        return failure;
                    }
                    finally
                    {
                        trace("done");
                    }
                }
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var statements = StatementDescendantsAndSelf(
            tree,
            GetOnlyMethod(tree).Body).ToArray();
        statements.ShouldContain(statement =>
            statement.Kind == Avm1StatementSyntaxKind.Try);
        statements.ShouldContain(statement =>
            statement.Kind == Avm1StatementSyntaxKind.With);
        statements.ShouldContain(statement =>
            statement.Kind == Avm1StatementSyntaxKind.Throw);
        var catchClause = statements.Single(statement =>
            statement.Kind == Avm1StatementSyntaxKind.CatchClause);
        tree.GetParameters(catchClause).Length.ShouldBe(1);
        statements.ShouldContain(statement =>
            statement.Kind == Avm1StatementSyntaxKind.FinallyClause);
    }

    [Fact]
    public void Statement_parser_applies_asi_without_swallowing_else_or_the_next_statement()
    {
        const string source = """
            class Test
            {
                function run():Number
                {
                    var value = getValue()
                    value = 1
                    if (value)
                        value++
                    else
                        value--
                    return value
                }
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeFalse(Describe(tree.Diagnostics, source));
        var body = tree.GetStatement(GetOnlyMethod(tree).Body);
        var children = tree.GetChildren(body).ToArray();
        children.Length.ShouldBe(4);
        tree.GetStatement(children[0]).Kind
            .ShouldBe(Avm1StatementSyntaxKind.VariableDeclaration);
        tree.GetStatement(children[1]).Kind
            .ShouldBe(Avm1StatementSyntaxKind.Expression);
        var conditional = tree.GetStatement(children[2]);
        conditional.Kind.ShouldBe(Avm1StatementSyntaxKind.If);
        tree.GetChildren(conditional).Length.ShouldBe(2);
        tree.GetStatement(children[3]).Kind
            .ShouldBe(Avm1StatementSyntaxKind.Return);
    }

    [Fact]
    public void Statement_parser_recovers_a_missing_local_initializer_at_the_semicolon()
    {
        const string source = """
            class Test
            {
                function run():Number
                {
                    var broken = ;
                    var good = 2;
                    return good;
                }
            }
            """;

        var tree = Avm1SyntaxTree.Parse(source);

        tree.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1S2001");
        var statements = StatementDescendantsAndSelf(
            tree,
            GetOnlyMethod(tree).Body).ToArray();
        var declarations = statements.Where(statement =>
            statement.Kind == Avm1StatementSyntaxKind.VariableDeclaration).ToArray();
        declarations.Length.ShouldBe(2);
        var good = declarations
            .Select(statement => tree.GetDeclarators(statement).ToArray().ShouldHaveSingleItem())
            .Single(declarator =>
                tree.GetTokenText(declarator.NameToken).ToString() == "good");
        tree.GetTokenText(good.NameToken).ToString().ShouldBe("good");
        tree.GetExpression(good.Initializer).Kind
            .ShouldBe(Avm1ExpressionSyntaxKind.LiteralExpression);
        statements.ShouldContain(statement =>
            statement.Kind == Avm1StatementSyntaxKind.Return);
    }

    [Fact]
    public void Parser_gates_directives_but_keeps_their_structure()
    {
        const string source = "class Test { #include \"part.as\"\n var value; }";
        var options = new Avm1ParseOptions
        {
            Features = Avm1SyntaxFeatures.None
        };

        var tree = Avm1SyntaxTree.Parse(source, options);

        tree.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1S1008");
        tree.Directives.Count.ShouldBe(1);
        tree.TokenStream.GetTokenizedText().ShouldBe(source);
    }

    [Fact]
    public void Parser_recovers_after_a_malformed_member()
    {
        const string source =
            "class Broken { var ; function good():Void { return; } }";

        var tree = Avm1SyntaxTree.Parse(source);

        tree.HasErrors.ShouldBeTrue();
        var members = tree.GetMembers(tree.Types.ShouldHaveSingleItem()).ToArray();
        members.Any(member =>
            member.Kind == Avm1MemberDeclarationKind.Method &&
            tree.GetTokenText(member.NameToken).ToString() == "good")
            .ShouldBeTrue();
    }

    [Fact]
    public void Parses_the_complete_skyui_source_target_losslessly()
    {
        var files = SkyUiSourceRoots
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.as",
                SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        files.Length.ShouldBe(110);

        var failures = new List<string>();
        var parsedInitializers = 0;
        var parsedMethodBodies = 0;
        var statementKinds = new HashSet<Avm1StatementSyntaxKind>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var tree = Avm1SyntaxTree.Parse(source);
            if (tree.TokenStream.GetTokenizedText() != source)
                failures.Add(file + ": tokenized text differs from the source");
            if (tree.HasErrors)
            {
                failures.Add(file + Environment.NewLine +
                    Describe(tree.Diagnostics, source));
            }
            if (tree.Types.Count != 1)
                failures.Add(file + $": expected one type, found {tree.Types.Count}");

            foreach (var expression in tree.Expressions)
            {
                if (expression.Index.Value < 0 ||
                    expression.Index.Value >= tree.Expressions.Count)
                {
                    failures.Add(file + ": invalid expression index");
                    continue;
                }
                if (!expression.Span.IsValid || expression.Span.End > source.Length)
                    failures.Add(file + $": invalid expression span {expression.Span}");
                foreach (var child in tree.GetChildren(expression))
                {
                    if (!child.IsValid || child.Value >= expression.Index.Value)
                    {
                        failures.Add(file +
                            $": expression {expression.Index.Value} has invalid child {child.Value}");
                    }
                }
                if (expression.Kind is Avm1ExpressionSyntaxKind.FunctionExpression)
                {
                    if (!expression.Body.IsValid)
                    {
                        failures.Add(file + ": function expression has no body root");
                    }
                    else
                    {
                        var body = tree.GetStatement(expression.Body);
                        if (body.Kind != Avm1StatementSyntaxKind.Block ||
                            body.Span != expression.BodySpan)
                        {
                            failures.Add(file + ": function-expression body differs");
                        }
                    }
                }
            }

            foreach (var statement in tree.Statements)
            {
                statementKinds.Add(statement.Kind);
                if (statement.Index.Value < 0 ||
                    statement.Index.Value >= tree.Statements.Count)
                {
                    failures.Add(file + ": invalid statement index");
                    continue;
                }
                if (!statement.Span.IsValid || statement.Span.End > source.Length)
                    failures.Add(file + $": invalid statement span {statement.Span}");
                foreach (var child in tree.GetChildren(statement))
                {
                    if (!child.IsValid || child.Value >= statement.Index.Value)
                    {
                        failures.Add(file +
                            $": statement {statement.Index.Value} has invalid child {child.Value}");
                    }
                }
                foreach (var expression in tree.GetInitializers(statement))
                {
                    if (!expression.IsValid || expression.Value >= tree.Expressions.Count)
                        failures.Add(file + ": invalid statement initializer expression");
                }
                foreach (var expression in tree.GetExpressions(statement))
                {
                    if (!expression.IsValid || expression.Value >= tree.Expressions.Count)
                        failures.Add(file + ": invalid statement expression list");
                }
                if (statement.Expression.IsValid &&
                    statement.Expression.Value >= tree.Expressions.Count)
                {
                    failures.Add(file + ": invalid primary statement expression");
                }
                if (statement.SecondaryExpression.IsValid &&
                    statement.SecondaryExpression.Value >= tree.Expressions.Count)
                {
                    failures.Add(file + ": invalid secondary statement expression");
                }
            }

            foreach (var type in tree.Types)
            {
                foreach (var member in tree.GetMembers(type))
                {
                    if (member.BodySpan.IsValid)
                    {
                        parsedMethodBodies++;
                        if (!member.Body.IsValid)
                        {
                            failures.Add(file + ": method body has no statement root");
                        }
                        else
                        {
                            var body = tree.GetStatement(member.Body);
                            if (body.Kind != Avm1StatementSyntaxKind.Block)
                                failures.Add(file + ": method root is not a block");
                            if (body.Span != member.BodySpan)
                                failures.Add(file + ": method root span differs");
                        }
                    }
                    foreach (var declarator in tree.GetDeclarators(member))
                    {
                        if (!declarator.InitializerSpan.IsValid)
                            continue;
                        parsedInitializers++;
                        if (!declarator.Initializer.IsValid)
                        {
                            failures.Add(file + ": initializer has no expression root");
                            continue;
                        }
                        if (tree.GetExpression(declarator.Initializer).Span !=
                            declarator.InitializerSpan)
                        {
                            failures.Add(file + ": initializer root span differs");
                        }
                    }
                    foreach (var parameter in tree.GetParameters(member))
                    {
                        if (parameter.DefaultValueSpan.IsValid &&
                            !parameter.DefaultValue.IsValid)
                        {
                            failures.Add(file + ": default value has no expression root");
                        }
                    }
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
        parsedInitializers.ShouldBeGreaterThan(0);
        parsedMethodBodies.ShouldBeGreaterThan(500);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.If);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.For);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.ForIn);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.Switch);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.While);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.DoWhile);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.Break);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.Continue);
        statementKinds.ShouldContain(Avm1StatementSyntaxKind.Return);
    }

    private static Avm1MemberDeclarationSyntax GetOnlyMethod(Avm1SyntaxTree tree)
    {
        var type = tree.Types.ShouldHaveSingleItem();
        return tree.GetMembers(type).ToArray().ShouldHaveSingleItem();
    }

    private static Avm1ExpressionIndex GetOnlyInitializer(Avm1SyntaxTree tree)
    {
        var type = tree.Types.ShouldHaveSingleItem();
        var field = tree.GetMembers(type).ToArray().ShouldHaveSingleItem();
        return tree.GetDeclarators(field).ToArray().ShouldHaveSingleItem().Initializer;
    }

    private static string GetTokenText(
        Avm1SyntaxTree tree,
        Avm1ExpressionIndex expression)
    {
        var node = tree.GetExpression(expression);
        return tree.GetTokenText(node.Token).ToString();
    }

    private static IEnumerable<Avm1ExpressionSyntax> DescendantsAndSelf(
        Avm1SyntaxTree tree,
        Avm1ExpressionIndex root)
    {
        var pending = new Stack<Avm1ExpressionIndex>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var expression = tree.GetExpression(pending.Pop());
            yield return expression;
            foreach (var child in tree.GetChildren(expression))
                pending.Push(child);
        }
    }

    private static IEnumerable<Avm1StatementSyntax> StatementDescendantsAndSelf(
        Avm1SyntaxTree tree,
        Avm1StatementIndex root)
    {
        var pending = new Stack<Avm1StatementIndex>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var statement = tree.GetStatement(pending.Pop());
            yield return statement;
            foreach (var child in tree.GetChildren(statement))
                pending.Push(child);
        }
    }

    private static string Describe(
        IReadOnlyList<Avm1SyntaxDiagnostic> diagnostics,
        string source) =>
        string.Join(
            Environment.NewLine,
            diagnostics.Select(diagnostic =>
            {
                var text = diagnostic.Span.IsValid &&
                           diagnostic.Span.End <= source.Length
                    ? source.AsSpan(
                        diagnostic.Span.Start,
                        diagnostic.Span.Length).ToString()
                    : "<invalid span>";
                return $"{diagnostic.Code} {diagnostic.Span}: " +
                    $"{diagnostic.Message} [{text}]";
            }));
}
