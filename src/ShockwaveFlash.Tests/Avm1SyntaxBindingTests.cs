using ShockwaveFlash.Avm1.Compilation.Binding;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SyntaxBindingTests
{
    private static readonly string[] SkyUiSourceRoots =
    [
        @"C:\Users\artem\source\repos\skyui\src\CraftingMenu",
        @"C:\Users\artem\source\repos\skyui\src\Common",
        @"C:\Users\artem\source\repos\skyui\src\CLIK"
    ];

    [Fact]
    public void Declaration_index_preserves_grouped_fields_members_and_origins()
    {
        var program = CreateProgram(
            ("Helper.as", "class deps.Helper {}"),
            ("Example.as", """
                import deps.Helper;

                class sample.Example
                {
                    private static var first:Number = 1, second:Helper;
                    function Example() {}
                    function get value():Number { return first; }
                    function set value(next:Number):Void { first = next; }
                    function run(input:Helper):Helper { return input; }
                }
                """));

        program.HasErrors.ShouldBeFalse(Describe(program));
        var example = GetType(program, "sample.Example");
        program.GetName(program.GetSymbol(example).Name).ShouldBe("Example");
        var members = program.GetMembers(example)
            .ToArray()
            .Select(program.GetSymbol)
            .ToArray();
        members.Select(member => member.Kind).ShouldBe([
            Avm1SyntaxProgramSymbolKind.Field,
            Avm1SyntaxProgramSymbolKind.Field,
            Avm1SyntaxProgramSymbolKind.Constructor,
            Avm1SyntaxProgramSymbolKind.Getter,
            Avm1SyntaxProgramSymbolKind.Setter,
            Avm1SyntaxProgramSymbolKind.Method
        ]);
        members.Select(member => program.GetName(member.Name)).ShouldBe([
            "first",
            "second",
            "Example",
            "value",
            "value",
            "run"
        ]);

        members[0].Origin.MemberOrdinal.ShouldBe(members[1].Origin.MemberOrdinal);
        members[0].Origin.DeclaratorOrdinal.ShouldBe(0);
        members[1].Origin.DeclaratorOrdinal.ShouldBe(1);
        var second = program.GetFieldDeclarator(members[1].Index);
        var tree = program.GetSource(members[1].Origin.File).SyntaxTree;
        tree.GetTokenText(second.NameToken).ToString().ShouldBe("second");

        program.GetMemberCandidates(example, "value")
            .Select(index => program.GetSymbol(index).Kind)
            .ShouldBe([
                Avm1SyntaxProgramSymbolKind.Getter,
                Avm1SyntaxProgramSymbolKind.Setter
            ]);
        program.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code == "AVM1B1003");

        var import = program.GetImports(new Avm1SyntaxFileIndex(1))
            .ToArray()
            .ShouldHaveSingleItem();
        import.Kind.ShouldBe(Avm1SyntaxImportKind.Explicit);
        import.Target.Kind.ShouldBe(Avm1SyntaxSymbolResolutionKind.Bound);
        program.GetName(program.GetSymbol(import.Target.Symbol).QualifiedName)
            .ShouldBe("deps.Helper");
    }

    [Fact]
    public void Type_lookup_collects_explicit_package_wildcard_and_qualified_candidates()
    {
        var program = CreateProgram(
            ("AlphaFoo.as", "class alpha.Foo {}"),
            ("AlphaInterface.as", "interface alpha.IFace {}"),
            ("BetaFoo.as", "class beta.Foo {}"),
            ("ExplicitOwner.as", """
                import beta.Foo;
                class ExplicitOwner extends Foo {}
                """),
            ("PackageOwner.as", """
                class alpha.PackageOwner extends Foo implements IFace {}
                """),
            ("WildcardOwner.as", """
                import alpha.*;
                class WildcardOwner extends Foo {}
                """),
            ("AmbiguousOwner.as", """
                import beta.Foo;
                class alpha.AmbiguousOwner extends Foo {}
                """));

        program.HasErrors.ShouldBeTrue();
        program.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B1006")
            .ShouldBe(1);

        AssertBound(program, "ExplicitOwner", "Foo", "beta.Foo");
        AssertBound(program, "alpha.PackageOwner", "Foo", "alpha.Foo");
        AssertBound(program, "alpha.PackageOwner", "IFace", "alpha.IFace");
        AssertBound(program, "WildcardOwner", "Foo", "alpha.Foo");
        AssertBound(program, "ExplicitOwner", "alpha.Foo", "alpha.Foo");

        var ambiguousOwner = GetType(program, "alpha.AmbiguousOwner");
        var ambiguousFile = program.GetSymbol(ambiguousOwner).Origin.File;
        program.ResolveType(ambiguousFile, ambiguousOwner, "Foo").Kind
            .ShouldBe(Avm1SyntaxSymbolResolutionKind.Ambiguous);
        program.GetTypeCandidates(ambiguousFile, ambiguousOwner, "Foo")
            .Select(index => program.GetName(program.GetSymbol(index).QualifiedName))
            .ShouldBe(["beta.Foo", "alpha.Foo"], ignoreOrder: true);

        var packageFile = program.GetSymbol(
            GetType(program, "alpha.PackageOwner")).Origin.File;
        var typeUses = program.GetTypeUses(packageFile).ToArray();
        typeUses.Select(use => use.Kind).ShouldBe([
            Avm1SyntaxTypeUseKind.BaseClass,
            Avm1SyntaxTypeUseKind.ImplementedInterface
        ]);
        typeUses.ShouldAllBe(use =>
            use.Resolution.Kind == Avm1SyntaxSymbolResolutionKind.Bound);
    }

    [Fact]
    public void Declaration_collisions_keep_all_candidates_and_report_stable_diagnostics()
    {
        var program = CreateProgram(
            ("First.as", """
                class duplicate.Type
                {
                    function run():Void {}
                    function run(value:Number):Void {}
                    function get property():Number { return 0; }
                    function set property(value:Number):Void {}
                }
                """),
            ("Second.as", "class duplicate.Type {}"),
            ("Left.as", "class left.Name {}"),
            ("Right.as", "class right.Name {}"),
            ("Imports.as", """
                import left.Name;
                import right.Name;
                class ImportOwner {}
                """));

        program.HasErrors.ShouldBeTrue();
        program.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B1002")
            .ShouldBe(1);
        program.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B1003")
            .ShouldBe(1);
        program.Diagnostics.Count(diagnostic => diagnostic.Code == "AVM1B1004")
            .ShouldBe(1);
        program.GetDeclaredTypes("duplicate.Type").Count.ShouldBe(2);
        program.TryGetType("duplicate.Type", out _).ShouldBeFalse();

        var firstType = program.GetDeclaredTypes("duplicate.Type")[0];
        program.GetMemberCandidates(firstType, "run").Count.ShouldBe(2);
        program.GetMemberCandidates(firstType, "property").Count.ShouldBe(2);
    }

    [Fact]
    public void Indexes_the_complete_skyui_target_without_false_binding_errors()
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

        var program = Avm1SyntaxProgram.Create(sourceFiles);

        program.HasErrors.ShouldBeFalse(Describe(program));
        program.Files.Count.ShouldBe(110);
        program.Symbols.Count(symbol => symbol.Kind is
            Avm1SyntaxProgramSymbolKind.Class or
            Avm1SyntaxProgramSymbolKind.Interface).ShouldBe(110);

        var boundImports = 0;
        var unresolvedImports = 0;
        foreach (var import in program.Imports)
        {
            if (import.Kind is Avm1SyntaxImportKind.Wildcard)
            {
                import.Target.Kind.ShouldBe(
                    Avm1SyntaxSymbolResolutionKind.NotApplicable);
                continue;
            }

            var declared = program.GetDeclaredTypes(program.GetName(import.Name));
            if (declared.Count == 1)
            {
                import.Target.Kind.ShouldBe(
                    Avm1SyntaxSymbolResolutionKind.Bound);
                import.Target.Symbol.ShouldBe(declared[0]);
                boundImports++;
            }
            else
            {
                import.Target.Kind.ShouldBe(
                    Avm1SyntaxSymbolResolutionKind.Unresolved);
                unresolvedImports++;
            }
        }

        boundImports.ShouldBeGreaterThan(0);
        unresolvedImports.ShouldBeGreaterThan(0);
        program.TypeUses.ShouldNotContain(use =>
            use.Resolution.Kind == Avm1SyntaxSymbolResolutionKind.Ambiguous);
        program.TypeUses.Count(use =>
            use.Resolution.Kind == Avm1SyntaxSymbolResolutionKind.Bound)
            .ShouldBeGreaterThan(0);
        program.TypeUses.Count(use =>
            use.Resolution.Kind == Avm1SyntaxSymbolResolutionKind.Unresolved)
            .ShouldBeGreaterThan(0);
    }

    private static Avm1SyntaxProgram CreateProgram(
        params (string Path, string Source)[] files) =>
        Avm1SyntaxProgram.Create(files.Select(file =>
            new Avm1SyntaxSourceFile(
                file.Path,
                Avm1SyntaxTree.Parse(file.Source))));

    private static Avm1SyntaxProgramSymbolIndex GetType(
        Avm1SyntaxProgram program,
        string qualifiedName)
    {
        program.TryGetType(qualifiedName, out var type).ShouldBeTrue();
        return type;
    }

    private static void AssertBound(
        Avm1SyntaxProgram program,
        string ownerName,
        string referenceName,
        string expectedName)
    {
        var owner = GetType(program, ownerName);
        var file = program.GetSymbol(owner).Origin.File;
        var resolution = program.ResolveType(file, owner, referenceName);
        resolution.Kind.ShouldBe(Avm1SyntaxSymbolResolutionKind.Bound);
        program.GetName(program.GetSymbol(resolution.Symbol).QualifiedName)
            .ShouldBe(expectedName);
    }

    private static string Describe(Avm1SyntaxProgram program) =>
        string.Join(
            Environment.NewLine,
            program.Diagnostics.Select(diagnostic =>
                $"{program.GetPath(diagnostic.File)}:{diagnostic.Span}: " +
                $"{diagnostic.Code} {diagnostic.Message}"));
}
