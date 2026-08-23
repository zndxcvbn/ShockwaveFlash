using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compilation.Preprocessing;
using ShockwaveFlash.Avm1.Compilation.Syntax;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.IO.Binary;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Tags.DisplayList;
using ShockwaveFlash.Tags.Metadata;
using ShockwaveFlash.Types;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1CompilerServiceTests
{
    private static readonly string[] SkyUiSourceRoots =
    [
        @"C:\Users\artem\source\repos\skyui\src\CraftingMenu",
        @"C:\Users\artem\source\repos\skyui\src\Common",
        @"C:\Users\artem\source\repos\skyui\src\CLIK"
    ];

    [Fact]
    public void Preserves_block_nodes_for_try_clauses_and_with_statements()
    {
        var source = new Avm1CompilationSource(
            Path.GetFullPath(@"C:\project\Blocks.as"),
            """
            class Blocks
            {
                function execute(value)
                {
                    try
                    {
                        throw value;
                    }
                    catch (error)
                    {
                        value = error;
                        return value;
                    }
                    finally
                    {
                        value++;
                        trace(value);
                    }
                }

                function dynamicScope(scope)
                {
                    with (scope)
                    {
                        first = 1;
                        second = 2;
                    }
                }
            }
            """);

        var result = new Avm1CompilerService().Compile(
            source,
            new Avm1CompilerServiceOptions(7));

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.ProgramArtifact.ShouldNotBeNull().Classes.ShouldHaveSingleItem()
            .Methods.Count.ShouldBe(2);
    }

    [Fact]
    public void Compiles_includes_and_reuses_the_complete_query_pipeline()
    {
        var root = Path.GetFullPath(@"C:\project\Widget.as");
        var include = Path.GetFullPath(@"C:\project\version.as");
        var resolver = new Avm1DictionaryIncludeResolver([
            KeyValuePair.Create(include, "static var VERSION:Number = 1;\n")
        ]);
        var source = new Avm1CompilationSource(root, """
            class demo.Widget
            {
                #include "version.as"
                function value():Number { return VERSION; }
            }
            """);
        var options = new Avm1CompilerServiceOptions(7)
        {
            IncludeResolver = resolver
        };
        var service = new Avm1CompilerService();

        var first = service.Compile(source, options);
        var second = service.Compile(source, options);

        first.Succeeded.ShouldBeTrue(Describe(first));
        first.PreprocessedSources.ShouldHaveSingleItem().Source.Dependencies
            .ShouldHaveSingleItem().Path.ShouldBe(include);
        first.ProgramArtifact.ShouldNotBeNull().Classes.ShouldHaveSingleItem();
        first.CacheStatistics.ShouldBe(new Avm1CompilerServiceCacheStatistics(
            SyntaxTreeHits: 0,
            SyntaxTreeMisses: 1,
            FrontEndHits: 0,
            FrontEndMisses: 1,
            CompilationHits: 0,
            CompilationMisses: 1));
        second.Succeeded.ShouldBeTrue(Describe(second));
        second.CacheStatistics.FrontEndHits.ShouldBe(1);
        second.CacheStatistics.CompilationHits.ShouldBe(1);
        second.CacheStatistics.SyntaxTreeHits.ShouldBe(0);
        second.CacheStatistics.SyntaxTreeMisses.ShouldBe(0);
    }

    [Fact]
    public void Resolves_include_sequence_points_and_rebases_them_after_linking()
    {
        var root = Path.GetFullPath(@"C:\project\MappedWidget.as");
        var include = Path.GetFullPath(@"C:\project\value-method.as");
        const string includeText = """
            function value()
            {
                return 42;
            }
            """;
        var source = new Avm1CompilationSource(root, """
            class demo.MappedWidget
            {
                #include "value-method.as"
            }
            """);
        var options = new Avm1CompilerServiceOptions(7)
        {
            IncludeResolver = new Avm1DictionaryIncludeResolver([
                KeyValuePair.Create(include, includeText)
            ])
        };
        var service = new Avm1CompilerService();
        var debugId = new Guid("00112233-4455-6677-8899-aabbccddeeff");

        var first = service.Compile(source, options);
        var cached = service.Compile(source, options);

        first.Succeeded.ShouldBeTrue(Describe(first));
        cached.Succeeded.ShouldBeTrue(Describe(cached));
        var artifact = first.ProgramArtifact.ShouldNotBeNull().Classes
            .ShouldHaveSingleItem();
        first.DebugMap.Documents.Select(document => document.Path)
            .ShouldBe([root, include]);
        first.DebugMap.MappedEntryCount.ShouldBe(artifact.SourceMap.Entries.Count);
        first.DebugMap.UnresolvedEntryCount.ShouldBe(0);
        first.DebugMap.SequencePoints.ShouldNotBeEmpty();
        cached.DebugMap.Documents.Select(document => (document.Path, document.Text))
            .ShouldBe(first.DebugMap.Documents.Select(document =>
                (document.Path, document.Text)));
        cached.DebugMap.SequencePoints.ToArray()
            .ShouldBe(first.DebugMap.SequencePoints.ToArray());

        var includeDocument = first.DebugMap.Documents
            .Where(document => document.Path == include)
            .ShouldHaveSingleItem();
        var literalOffset = includeText.IndexOf("42", StringComparison.Ordinal);
        var literalPoint = first.DebugMap.SequencePoints
            .Where(point => point.Source.Document == includeDocument.Index)
            .Where(point => point.Source.Span == new Avm1TextSpan(
                literalOffset,
                "42".Length))
            .ShouldHaveSingleItem();
        literalPoint.Source.Lines.Start.Line.ShouldBe(3);
        literalPoint.Source.Lines.Start.Column.ShouldBe(12);
        first.DebugMap.TryGetSequencePoint(
                classIndex: 0,
                literalPoint.ByteOffset,
                out var classResolved)
            .ShouldBeTrue();

        var swf = CreateSwf(
            new ShowFrameTag(new TagMetadata(TagCode.ShowFrame, 0, 0)),
            new EndTag(new TagMetadata(TagCode.End, 0, 0)));
        var linked = Avm1SwfLinker.Link(
            swf,
            new Avm1SwfPlacementPlan([
                new Avm1SwfPlacement(artifact, Avm1SwfPlacementKind.DoAction)
            ]),
            new Avm1SwfLinkOptions
            {
                Debugger = new Avm1SwfDebuggerOptions(debugId)
            });

        linked.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            linked.Diagnostics.Select(diagnostic => diagnostic.Message)));
        linked.Debugger.ShouldBe(new Avm1SwfDebuggerInfo(debugId, string.Empty));
        swf.Tags.OfType<EnableDebugger2Tag>().ShouldHaveSingleItem()
            .Password.ShouldBeEmpty();
        swf.Tags.OfType<DebugIdTag>().ShouldHaveSingleItem()
            .Id.ShouldBe(debugId);
        var placement = linked.SourceMap.Placements.ShouldHaveSingleItem();
        var headerWriter = new MemoryWriter();
        swf.Header.Encode(headerWriter);
        linked.SourceMap.UncompressedTagStreamFileOffset
            .ShouldBe(8 + headerWriter.Position);
        var linkedMap = first.DebugMap.CreateLinkedMap(linked);
        var expectedOffset = checked(
            placement.ActionStreamOffset + classResolved.ByteOffset);
        linkedMap.TryGetSequencePoint(expectedOffset, out var linkedResolved)
            .ShouldBeTrue();
        linkedResolved.TagStreamOffset.ShouldBe(expectedOffset);
        linkedResolved.ClassPoint.ShouldBe(classResolved);
        linkedResolved.Source.ShouldBe(classResolved.Source);

        var swd = Avm1SwdBuilder.Build(first.DebugMap, linked);
        var decodedSwd = Avm1SwdCodec.Decode(swd.Bytes);
        decodedSwd.Records.OfType<Avm1SwdDebugIdRecord>()
            .ShouldHaveSingleItem().Id.ShouldBe(debugId);
        var sourceRecord = decodedSwd.Records
            .OfType<Avm1SwdSourceFileRecord>()
            .Where(record => record.Path == include)
            .ShouldHaveSingleItem();
        sourceRecord.SourceText.ShouldBe(includeText);
        var lineRecord = decodedSwd.Records
            .OfType<Avm1SwdOffsetMapRecord>()
            .Where(record =>
                record.ModuleId == sourceRecord.ModuleId &&
                record.Line == 3)
            .ShouldHaveSingleItem();
        lineRecord.SwfByteOffset.ShouldBe(checked((uint)(
            linked.SourceMap.UncompressedTagStreamFileOffset + expectedOffset)));
        lineRecord.SwfByteOffset.ShouldBeLessThan((uint)swf.Assemble().Length);
    }

    [Fact]
    public void Parallelism_validation_cannot_be_bypassed_by_compilation_cache()
    {
        var source = new Avm1CompilationSource(
            Path.GetFullPath(@"C:\project\Parallelism.as"),
            "class Parallelism { function value() { return 1; } }");
        var service = new Avm1CompilerService();
        var valid = service.Compile(
            source,
            new Avm1CompilerServiceOptions(7)
            {
                MaxDegreeOfParallelism = 1
            });

        var invalid = service.Compile(
            source,
            new Avm1CompilerServiceOptions(7)
            {
                MaxDegreeOfParallelism = 0
            });

        valid.Succeeded.ShouldBeTrue(Describe(valid));
        invalid.Succeeded.ShouldBeFalse();
        invalid.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == "AVM1CLS007");
        invalid.CacheStatistics.FrontEndHits.ShouldBe(1);
        invalid.CacheStatistics.CompilationHits.ShouldBe(0);
        invalid.CacheStatistics.CompilationMisses.ShouldBe(1);
    }

    [Fact]
    public void Include_and_file_changes_invalidate_only_affected_queries()
    {
        var firstPath = Path.GetFullPath(@"C:\project\First.as");
        var secondPath = Path.GetFullPath(@"C:\project\Second.as");
        var includePath = Path.GetFullPath(@"C:\project\value.as");
        var firstSource = new Avm1CompilationSource(firstPath, """
            class First
            {
                #include "value.as"
            }
            """);
        var secondSource = new Avm1CompilationSource(
            secondPath,
            "class Second { function value():Number { return 2; } }");
        var service = new Avm1CompilerService();
        var firstOptions = new Avm1CompilerServiceOptions(7)
        {
            IncludeResolver = new Avm1DictionaryIncludeResolver([
                KeyValuePair.Create(
                    includePath,
                    "static var VALUE:Number = 1;\n")
            ])
        };
        var changedOptions = firstOptions with
        {
            IncludeResolver = new Avm1DictionaryIncludeResolver([
                KeyValuePair.Create(
                    includePath,
                    "static var VALUE:Number = 3;\n")
            ])
        };

        var first = service.Compile(
            [firstSource, secondSource],
            firstOptions);
        var changed = service.Compile(
            [firstSource, secondSource],
            changedOptions);

        first.Succeeded.ShouldBeTrue(Describe(first));
        changed.Succeeded.ShouldBeTrue(Describe(changed));
        changed.PreprocessedSources[0].Source.Text.ShouldContain("VALUE:Number = 3");
        changed.CacheStatistics.FrontEndMisses.ShouldBe(1);
        changed.CacheStatistics.CompilationMisses.ShouldBe(1);
        changed.CacheStatistics.SyntaxTreeHits.ShouldBe(1);
        changed.CacheStatistics.SyntaxTreeMisses.ShouldBe(1);
    }

    [Fact]
    public void Reference_abi_changes_invalidate_binding_but_reuse_syntax()
    {
        var source = new Avm1CompilationSource(
            Path.GetFullPath(@"C:\project\Test.as"),
            "import ext.Widget; class Test { var value:Widget; }");
        var firstReferences = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("ext.Widget"))
        ]);
        var changedReferences = new Avm1ReferenceCatalog([
            new Avm1SourceReferenceClass(
                new Avm1SourceQualifiedName("ext.Widget"),
                members:
                [
                    new Avm1SourceReferenceMember(
                        "version",
                        Avm1SourceReferenceMemberKind.Field,
                        new Avm1SourceTypeReference(
                            new Avm1SourceQualifiedName("Number")))
                ])
        ]);
        var service = new Avm1CompilerService();

        var first = service.Compile(
            source,
            new Avm1CompilerServiceOptions(7)
            {
                ReferenceProvider = firstReferences
            });
        var changed = service.Compile(
            source,
            new Avm1CompilerServiceOptions(7)
            {
                ReferenceProvider = changedReferences
            });

        first.Succeeded.ShouldBeTrue(Describe(first));
        changed.Succeeded.ShouldBeTrue(Describe(changed));
        changed.CacheStatistics.FrontEndMisses.ShouldBe(1);
        changed.CacheStatistics.SyntaxTreeHits.ShouldBe(1);
        changed.SourceProgram.TryGetClassSymbol(
            "ext.Widget",
            out var widget).ShouldBeTrue();
        changed.SourceProgram.Symbols.ShouldContain(symbol =>
            symbol.ContainingSymbol == widget && symbol.Name == "version");
    }

    [Fact]
    public void Diagnostics_from_includes_map_back_to_the_included_file()
    {
        var root = Path.GetFullPath(@"C:\project\Test.as");
        var include = Path.GetFullPath(@"C:\project\broken.as");
        var service = new Avm1CompilerService();
        var result = service.Compile(
            new Avm1CompilationSource(root, """
                class Test
                {
                    #include "broken.as"
                }
                """),
            new Avm1CompilerServiceOptions(7)
            {
                IncludeResolver = new Avm1DictionaryIncludeResolver([
                    KeyValuePair.Create(include, "function broken( {\n")
                ])
            });

        result.Succeeded.ShouldBeFalse();
        result.ProgramArtifact.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Stage == Avm1CompilerServiceStage.Syntax &&
            diagnostic.Path == include &&
            diagnostic.Span.IsValid);
    }

    [Fact]
    public void Compiles_the_complete_authored_skyui_program_incrementally()
    {
        var paths = SkyUiSourceRoots
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.as",
                SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        paths.Length.ShouldBe(110);
        var service = new Avm1CompilerService();
        var options = new Avm1CompilerServiceOptions(7)
        {
            IncludeResolver = new Avm1FileSystemIncludeResolver([
                @"C:\Users\artem\source\repos\skyui\src"
            ])
        };

        var first = service.CompileFiles(paths, options);
        var cached = service.CompileFiles(paths, options);

        first.Succeeded.ShouldBeTrue(Describe(first));
        first.PreprocessedSources.Count.ShouldBe(110);
        first.PreprocessedSources.Sum(item => item.Source.Dependencies.Count)
            .ShouldBe(2);
        first.SourceProgram.Files.SelectMany(file => file.Classes).Count()
            .ShouldBe(110);
        first.SourceProgram.Files.SelectMany(file => file.Classes).Count(type =>
            type.Kind == Avm1SourceTypeDeclarationKind.Interface).ShouldBe(3);
        first.ProgramArtifact.ShouldNotBeNull().Classes.Count.ShouldBe(110);
        first.ProgramArtifact.Classes.ShouldAllBe(sourceClass =>
            sourceClass.Succeeded);
        first.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == Avm1CompilationDiagnosticSeverity.Error);
        cached.Succeeded.ShouldBeTrue(Describe(cached));
        cached.CacheStatistics.FrontEndHits.ShouldBe(1);
        cached.CacheStatistics.CompilationHits.ShouldBe(1);
        cached.CacheStatistics.SyntaxTreeHits.ShouldBe(0);
        cached.CacheStatistics.SyntaxTreeMisses.ShouldBe(0);
    }

    private static string Describe(Avm1CompilerServiceResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage} {diagnostic.Code} " +
                $"{diagnostic.Path}:{diagnostic.Span}: {diagnostic.Message}"));

    private static ShockwaveFlashFile CreateSwf(params Tag[] tags) =>
        new(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                Version: 7,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
            tags.ToList());
}
