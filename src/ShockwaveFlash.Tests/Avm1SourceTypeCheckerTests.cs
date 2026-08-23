using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Binding;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using ShockwaveFlash.Avm1.Source;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SourceTypeCheckerTests
{
    [Fact]
    public void Conservative_mode_reports_only_proven_nominal_and_arity_issues()
    {
        const string source = """
            import mx.utils.Delegate;

            class Test
            {
                var missing:pkg.Missing;

                function run(callback):Void
                {
                    Delegate.create(this);
                    callback();
                }
            }
            """;
        var program = Project(source, Avm1ReferenceCatalogs.Default);

        var result = Avm1SourceTypeChecker.Check(program);

        result.HasErrors.ShouldBeFalse();
        result.Diagnostics.Select(diagnostic => diagnostic.Code)
            .ShouldBe(["AVM1TYP001", "AVM1TYP002"], ignoreOrder: true);
        result.Diagnostics.ShouldAllBe(diagnostic =>
            diagnostic.Severity == Avm1SourceDiagnosticSeverity.Warning);
        result.Diagnostics.Count(diagnostic =>
            diagnostic.Code == "AVM1TYP002").ShouldBe(1);
    }

    [Fact]
    public void Strict_mode_promotes_policy_diagnostics_to_errors()
    {
        const string source = """
            interface IContract {}
            class Base {}
            class Wrong extends IContract implements Base {}
            """;
        var program = Project(source);

        var conservative = Avm1SourceTypeChecker.Check(program);
        var strict = Avm1SourceTypeChecker.Check(
            program,
            Avm1SourceTypeCheckingMode.Strict);

        conservative.HasErrors.ShouldBeFalse();
        conservative.Diagnostics.Count(diagnostic =>
            diagnostic.Code == "AVM1TYP004").ShouldBe(2);
        strict.HasErrors.ShouldBeTrue();
        strict.Diagnostics.Where(diagnostic => diagnostic.Code == "AVM1TYP004")
            .ShouldAllBe(diagnostic =>
                diagnostic.Severity == Avm1SourceDiagnosticSeverity.Error);
    }

    [Fact]
    public void Inheritance_cycles_are_always_rejected()
    {
        const string source = """
            interface IFirst extends ISecond {}
            interface ISecond extends IFirst {}
            """;
        var program = Project(source);

        var result = Avm1SourceTypeChecker.Check(program);

        result.HasErrors.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1TYP005" &&
            diagnostic.Message.Contains("IFirst", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("ISecond", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_mode_does_not_force_semantic_analysis()
    {
        var program = Project("class Test {}");

        var result = Avm1SourceTypeChecker.Check(
            program,
            Avm1SourceTypeCheckingMode.None);

        result.Diagnostics.ShouldBeEmpty();
        result.HasErrors.ShouldBeFalse();
    }

    private static Avm1SourceProgram Project(
        string source,
        IAvm1ReferenceProvider? references = null)
    {
        var syntax = Avm1SyntaxProgram.Create([
            new Avm1SyntaxSourceFile("Test.as", Avm1SyntaxTree.Parse(source))
        ]);
        var projection = Avm1SyntaxSourceProjector.Project(
            syntax,
            referenceProvider: references);
        projection.HasErrors.ShouldBeFalse(string.Join(
            Environment.NewLine,
            projection.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        return projection.SourceProgram;
    }
}
