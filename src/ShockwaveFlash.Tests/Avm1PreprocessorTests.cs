using ShockwaveFlash.Avm1.Compilation.Preprocessing;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1PreprocessorTests
{
    [Fact]
    public void Expands_nested_includes_and_maps_spans_to_their_source_file()
    {
        var root = GetPath("Main.as");
        var members = GetPath("members.as");
        var methods = GetPath(Path.Combine("nested", "methods.as"));
        const string source = """
            class demo.Main
            {
                #include "members.as"
            }
            """;
        const string memberSource = """
            public var value:Number;
            #include "nested/methods.as"
            """;
        const string methodSource = """
            public function getValue():Number
            {
                return value;
            }
            """;
        var resolver = new Avm1DictionaryIncludeResolver([
            KeyValuePair.Create(members, memberSource),
            KeyValuePair.Create(methods, methodSource)
        ]);

        var result = Avm1Preprocessor.Expand(root, source, resolver);

        result.HasErrors.ShouldBeFalse(Describe(result));
        result.Source.Text.ShouldNotContain("#include");
        result.Source.Dependencies.Select(dependency => dependency.Path)
            .ShouldBe([members, methods], ignoreOrder: true);
        result.Source.Documents.Select(document => document.Path)
            .ShouldBe([root, members, methods]);
        result.Source.TryGetDocument(methods, out var methodDocument)
            .ShouldBeTrue();
        methodDocument.Text.ShouldBe(methodSource);
        var returnOffset = methodSource.IndexOf("return", StringComparison.Ordinal);
        methodDocument.GetLinePosition(returnOffset)
            .ShouldBe(new Avm1SourceLinePosition(3, 5));
        methodDocument.GetLineSpan(new Avm1TextSpan(
                returnOffset,
                "return value".Length))
            .ShouldBe(new Avm1SourceLineSpan(
                new Avm1SourceLinePosition(3, 5),
                new Avm1SourceLinePosition(3, 17)));
        var tree = Avm1SyntaxTree.Parse(result.Source.Text);
        tree.HasErrors.ShouldBeFalse(string.Join(
            Environment.NewLine,
            tree.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var type = tree.Types.ShouldHaveSingleItem();
        tree.GetMembers(type).Length.ShouldBe(2);

        var expandedOffset = result.Source.Text.IndexOf(
            "value",
            StringComparison.Ordinal);
        var location = result.Source.MapLocation(
            new Avm1TextSpan(expandedOffset, "value".Length));
        location.Path.ShouldBe(members);
        location.Span.ShouldBe(new Avm1TextSpan(
            memberSource.IndexOf("value", StringComparison.Ordinal),
            "value".Length));
    }

    [Fact]
    public void Physical_documents_track_mixed_line_endings()
    {
        const string source = "first\r\nsecond\rthird\nfourth";
        var result = Avm1Preprocessor.Expand(GetPath("Lines.as"), source);

        var document = result.Source.Documents.ShouldHaveSingleItem();

        document.LineCount.ShouldBe(4);
        document.GetLinePosition(0)
            .ShouldBe(new Avm1SourceLinePosition(1, 1));
        document.GetLinePosition(source.IndexOf("second", StringComparison.Ordinal))
            .ShouldBe(new Avm1SourceLinePosition(2, 1));
        document.GetLinePosition(source.IndexOf("third", StringComparison.Ordinal))
            .ShouldBe(new Avm1SourceLinePosition(3, 1));
        document.GetLinePosition(source.IndexOf("fourth", StringComparison.Ordinal))
            .ShouldBe(new Avm1SourceLinePosition(4, 1));
        document.GetLinePosition(source.Length)
            .ShouldBe(new Avm1SourceLinePosition(4, 7));
    }

    [Fact]
    public void Reports_recursive_include_cycles_without_recursing_forever()
    {
        var root = GetPath("Main.as");
        var nested = GetPath("nested.as");
        const string source = "#include \"nested.as\"\nclass Main {}";
        const string nestedSource = "#include \"Main.as\"";
        var resolver = new Avm1DictionaryIncludeResolver([
            KeyValuePair.Create(root, source),
            KeyValuePair.Create(nested, nestedSource)
        ]);

        var result = Avm1Preprocessor.Expand(root, source, resolver);

        result.HasErrors.ShouldBeTrue();
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("AVM1PP004");
        result.Source.Text.ShouldContain("class Main {}");
    }

    [Fact]
    public void Reports_an_include_when_no_resolver_is_configured()
    {
        var result = Avm1Preprocessor.Expand(
            GetPath("Main.as"),
            "#include \"missing.as\"\nclass Main {}");

        result.HasErrors.ShouldBeTrue();
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("AVM1PP002");
        result.Source.Text.ShouldNotContain("#include");
        Avm1SyntaxTree.Parse(result.Source.Text).HasErrors.ShouldBeFalse();
    }

    private static string GetPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "ShockwaveFlash.Avm1.Preprocessor.Tests",
            relativePath));

    private static string Describe(Avm1PreprocessorResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location.Path}:" +
                $"{diagnostic.Location.Span} {diagnostic.Message}"));
}
