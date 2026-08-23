using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ShockwaveFlash;
using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compatibility;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Swf1;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Text;
using ShockwaveFlash.Avm1.Types;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Action;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Tags.DisplayList;
using ShockwaveFlash.Tags.Metadata;
using ShockwaveFlash.Tests.Framework;
using ShockwaveFlash.Types;
using ShockwaveFlash.Types.Control;
using Shouldly;

namespace ShockwaveFlash.Tests;

[Collection(AdobeFlashCs6Collection.Name)]
public sealed class Avm1AdobeCs6CompatibilityTests
{
    private const string OracleOutputEnvironmentVariable =
        "SHOCKWAVEFLASH_ADOBE_ORACLE_OUTPUT";
    private const string CandidateOutputEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_CANDIDATE_OUTPUT";
    private const string FixtureDirectory =
        "Avm1/Compatibility/AdobeFlashCs6";

    [Fact]
    public void Checked_adobe_top_level_has_no_unknown_actions()
    {
        var swf = ReadOracleSwf();
        var unknown = new List<ActionOpcode>();
        foreach (var tag in swf.Tags.OfType<DoActionTag>())
        {
            var actions = ShockwaveFlash.Avm1.Action.DecodeCollection(
                tag.Data,
                swf.Header.Version,
                strict: true);
            var instructions = Avm1InstructionTable.Build(
                actions,
                swf.Header.Version);
            var stackIr = Avm1StackIr.Build(instructions);
            unknown.AddRange(stackIr.Instructions
                .Where(instruction => instruction.Op is
                    Avm1StackIrOp.UnknownAction)
                .Select(instruction =>
                    instructions[instruction.Action].Action.Opcode));
        }

        unknown.ShouldBeEmpty();
    }

    private static readonly ProbeCall[] ProbeCalls =
    [
        new(
            "phase6Arithmetic",
            [PushValue.Integer(3), PushValue.Integer(4)],
            "14"),
        new("phase6Loop", [PushValue.Integer(5)], "24"),
        new("phase6Classify", [PushValue.Integer(2)], "small"),
        new("phase6Finally", [PushValue.Integer(3)], "15"),
        new(
            "phase6Completion",
            [PushValue.Integer(-3)],
            "3"),
        new("phase6ComputedUpdate", [PushValue.Integer(0)], "12"),
        new("phase6FinallyReturn", [PushValue.Integer(3)], "13"),
        new("phase6FinallyThrow", [PushValue.Integer(3)], "6"),
        new("phase6NestedFinally", [PushValue.Integer(3)], "4"),
        new(
            "phase6LoopFinally",
            [PushValue.Integer(5)],
            Expected: "42",
            RuffleOracleExpected: "56"),
        new("phase6Rethrow", [PushValue.Integer(3)], "6"),
        new("phase6NestedLoops", [PushValue.Integer(3)], "30"),
        new("phase6SwitchLoop", [PushValue.Integer(5)], "46"),
        new(
            "phase6CatchLoop",
            [PushValue.Integer(5)],
            Expected: "22",
            RuffleOracleExpected: "56"),
        new(
            "phase6NestedLoopExit",
            [PushValue.Integer(5)],
            Expected: "26",
            RuffleOracleExpected: "36"),
        new(
            "phase6FinallyOverridesContinue",
            [PushValue.Integer(5)],
            Expected: "10",
            RuffleOracleExpected: "49"),
        new("phase6Coercions", [PushValue.Integer(3)], "true:false:5.5"),
        new(
            "phase6CallOrder",
            [],
            Expected: "abc:abc",
            RuffleOracleExpected: "cba:abc"),
        new(
            "phase6LiteralOrder",
            [],
            Expected: "abcd:abcd",
            RuffleOracleExpected: "bacd:abcd"),
        new(
            "phase6ComputedLValueOrder",
            [],
            Expected: "rk:3",
            RuffleOracleExpected: "rkrk:3"),
        new("phase6AccessorSemantics", [], "gs:3"),
        new("phase6PrototypeDispatch", [], "8"),
        new("phase6UnaryBits", [PushValue.Integer(3)], "-4:15:3:2"),
        new("phase6DeleteTypeof", [], "number:true:undefined"),
        new(
            "phase6ArgumentsAlias",
            [PushValue.Integer(3), PushValue.Integer(4)],
            "234"),
        new("phase6MethodReceiver", [], "7:12"),
        new(
            "phase6ConstructorOrder",
            [],
            Expected: "ab:ab",
            RuffleOracleExpected: "ba:ab"),
        new("phase6InstanceOf", [], "true:true:function")
    ];

    [Fact]
    public void Compiler_methods_are_source_compatible_with_the_cs6_oracle()
    {
        var oracleSwf = ReadOracleSwf();
        var oracleFunctions = ExtractFunctions(oracleSwf);
        var candidate = CompileCandidate(oracleSwf.Header.Version);

        oracleFunctions.Keys.Order(StringComparer.Ordinal).ShouldBe(
            ProbeCalls.Select(call => call.Name).Order(StringComparer.Ordinal));
        candidate.Methods.Select(method => method.Declaration.Name)
            .Order(StringComparer.Ordinal).ShouldBe(
                oracleFunctions.Keys.Order(StringComparer.Ordinal));

        var failures = new List<string>();
        foreach (var method in candidate.Methods)
        {
            var oracle = oracleFunctions[method.Declaration.Name];
            var result = Avm1MethodCompatibilityAnalyzer.Compare(
                oracle.ToCompatibilityInput(),
                ToCompatibilityInput(method, oracleSwf.Header.Version));
            result.BytecodeIsValid.ShouldBeTrue(
                Describe(result) + Environment.NewLine +
                "Reference P-code:" + Environment.NewLine +
                Avm1Disassembler.Disassemble(
                    oracle.Bytecode,
                    oracleSwf.Header.Version) + Environment.NewLine +
                "Candidate P-code:" + Environment.NewLine +
                Avm1Disassembler.Disassemble(
                    method.Method.Bytecode,
                    oracleSwf.Header.Version));
            result.SourceEquivalence.ShouldNotBeNull();
            if (!result.IsCompatible)
            {
                failures.Add(
                    method.Declaration.Name + Environment.NewLine +
                    Describe(result) + Environment.NewLine +
                    "Reference P-code:" + Environment.NewLine +
                    Avm1Disassembler.Disassemble(
                        oracle.Bytecode,
                        oracleSwf.Header.Version) + Environment.NewLine +
                    "Reference CFG:" + Environment.NewLine +
                    FormatControlFlow(
                        oracle.Bytecode,
                        oracleSwf.Header.Version,
                        oracle.FunctionContext) + Environment.NewLine +
                    "Candidate P-code:" + Environment.NewLine +
                    Avm1Disassembler.Disassemble(
                        method.Method.Bytecode,
                        oracleSwf.Header.Version) + Environment.NewLine +
                    "Candidate CFG:" + Environment.NewLine +
                    FormatControlFlow(
                        method.Method.Bytecode,
                        oracleSwf.Header.Version,
                        new FunctionContext(
                            method.FunctionFlags,
                            method.Parameters)));
            }
        }
        failures.ShouldBeEmpty(string.Join(
            Environment.NewLine + Environment.NewLine,
            failures));
    }

    [Fact]
    public void Source_projection_keeps_reused_method_receivers_distinct()
    {
        var swfVersion = ReadOracleSwf().Header.Version;
        var compiled = CompileCandidate(swfVersion).Methods.Single(method =>
            method.Declaration.Name == "phase6MethodReceiver");
        var source = Avm1Decompiler.DecompileMethod(
                compiled.Method.Bytecode,
                swfVersion,
                new FunctionContext(compiled.FunctionFlags, compiled.Parameters))
            .ProjectSource()
            .GetAs2Text();
        var firstReceiver = Regex.Match(
            source,
            @"(?<receiver>_loc2_(?:v\d+)?)\.add\(3\)");
        var secondReceiver = Regex.Match(
            source,
            @"(?<receiver>_loc2_(?:v\d+)?)\.call\(");

        firstReceiver.Success.ShouldBeTrue(source);
        secondReceiver.Success.ShouldBeTrue(source);
        firstReceiver.Groups["receiver"].Value.ShouldNotBe(
            secondReceiver.Groups["receiver"].Value);
    }

    [Fact]
    public async Task Compiler_profiles_preserve_their_distinct_runtime_contracts()
    {
        var rufflePath = ResolveRufflePath();
        if (rufflePath is null)
            return;

        var oraclePath = GetFixturePath("Phase6Core.swf");
        var oracleSwf = ReadOracleSwf();
        var adobeCandidate = CompileCandidate(oracleSwf.Header.Version);
        var canonicalCandidate = CompileCandidate(
            oracleSwf.Header.Version,
            Avm1ClassAbiProfile.Canonical);
        var adobeCandidateSwf = CreateCandidateSwf(
            oracleSwf.Header.Version,
            adobeCandidate.Methods);
        var canonicalCandidateSwf = CreateCandidateSwf(
            oracleSwf.Header.Version,
            canonicalCandidate.Methods);
        var configuredCandidatePath = Environment.GetEnvironmentVariable(
            CandidateOutputEnvironmentVariable);
        var candidatePath = string.IsNullOrWhiteSpace(configuredCandidatePath)
            ? Path.Combine(
                Path.GetTempPath(),
                $"ShockwaveFlash-Phase6a-{Guid.NewGuid():N}.swf")
            : Path.GetFullPath(configuredCandidatePath);
        var deleteCandidate = string.IsNullOrWhiteSpace(configuredCandidatePath);
        var canonicalCandidatePath = Path.Combine(
            Path.GetTempPath(),
            $"ShockwaveFlash-Phase6a-canonical-{Guid.NewGuid():N}.swf");
        try
        {
            await File.WriteAllBytesAsync(
                candidatePath,
                adobeCandidateSwf.Assemble().ToArray());
            await File.WriteAllBytesAsync(
                canonicalCandidatePath,
                canonicalCandidateSwf.Assemble().ToArray());
            var oracleOutput = await RunRuffleAsync(rufflePath, oraclePath);
            var adobeCandidateOutput = await RunRuffleAsync(
                rufflePath,
                candidatePath);
            var canonicalCandidateOutput = await RunRuffleAsync(
                rufflePath,
                canonicalCandidatePath);
            foreach (var call in ProbeCalls)
            {
                var oracleMarker = $"PHASE6A:{call.Name}=" +
                    (call.RuffleOracleExpected ?? call.Expected);
                var adobeCandidateMarker = $"PHASE6A:{call.Name}=" +
                    (call.RuffleOracleExpected ?? call.Expected);
                var canonicalCandidateMarker =
                    $"PHASE6A:{call.Name}={call.Expected}";
                oracleOutput.Contains(oracleMarker, StringComparison.Ordinal)
                    .ShouldBeTrue(oracleOutput);
                adobeCandidateOutput.Contains(
                        adobeCandidateMarker,
                        StringComparison.Ordinal)
                    .ShouldBeTrue(adobeCandidateOutput);
                canonicalCandidateOutput.Contains(
                        canonicalCandidateMarker,
                        StringComparison.Ordinal)
                    .ShouldBeTrue(canonicalCandidateOutput);
            }
        }
        finally
        {
            if (deleteCandidate)
                File.Delete(candidatePath);
            File.Delete(canonicalCandidatePath);
        }
    }

    [Fact]
    public async Task Checked_oracle_matches_live_flash_cs6_when_available()
    {
        var source = await File.ReadAllTextAsync(
            GetFixturePath("Phase6Core.as"));
        var live = await AdobeFlashCs6Oracle.TryCompileAsync(source);
        if (live is null)
            return;

        var oracleOutput = Environment.GetEnvironmentVariable(
            OracleOutputEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(oracleOutput))
        {
            var outputPath = Path.GetFullPath(oracleOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, live.Swf);
        }

        var checkedFunctions = ExtractFunctions(ReadOracleSwf());
        var liveFunctions = ExtractFunctions(
            ShockwaveFlashFile.Disassemble(live.Swf));
        liveFunctions.Keys.Order(StringComparer.Ordinal).ShouldBe(
            checkedFunctions.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, expected) in checkedFunctions)
        {
            var result = Avm1MethodCompatibilityAnalyzer.Compare(
                expected.ToCompatibilityInput(),
                liveFunctions[name].ToCompatibilityInput());
            result.IsCompatible.ShouldBeTrue(Describe(result));
        }
    }

    [Fact]
    public void Method_core_allocations_stay_bounded_on_the_checked_corpus()
    {
        const long allocationBudget = 20 * 1024 * 1024;
        var functions = ExtractFunctions(ReadOracleSwf()).Values
            .OrderBy(function => function.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var function in functions)
        {
            _ = Avm1Decompiler.BuildMethodCore(
                function.Bytecode,
                function.SwfVersion,
                function.FunctionContext);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var function in functions)
        {
            _ = Avm1Decompiler.BuildMethodCore(
                function.Bytecode,
                function.SwfVersion,
                function.FunctionContext);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThan(
            allocationBudget,
            $"BuildMethodCore allocated {allocated:N0} bytes for " +
            $"{functions.Length} checked functions.");
    }

    [Fact]
    public void Compiler_allocations_stay_bounded_on_the_checked_corpus()
    {
        const long allocationBudget = 20 * 1024 * 1024;
        const byte swfVersion = 15;
        var path = GetFixturePath("Phase6Core.class.as");
        var frontEnd = new Avm1CompilerService().Compile(
            new Avm1CompilationSource(path, File.ReadAllText(path)),
            new Avm1CompilerServiceOptions(swfVersion)
            {
                AbiProfile = Avm1ClassAbiProfile.AdobeFlashCs6Compatible
            });
        frontEnd.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            frontEnd.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage} {diagnostic.Code}: " +
                diagnostic.Message)));
        var options = new Avm1ClassCompilationOptions(swfVersion)
        {
            AbiProfile = Avm1ClassAbiProfile.AdobeFlashCs6Compatible
        };
        _ = new Avm1Compiler().CompileProgram(frontEnd.SourceProgram, options);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var artifact = new Avm1Compiler().CompileProgram(
            frontEnd.SourceProgram,
            options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        artifact.Succeeded.ShouldBeTrue();
        allocated.ShouldBeLessThan(
            allocationBudget,
            $"CompileProgram allocated {allocated:N0} bytes for the checked " +
            "Flash CS6 corpus.");
    }

    private static Avm1ClassArtifact CompileCandidate(
        byte swfVersion,
        Avm1ClassAbiProfile abiProfile =
            Avm1ClassAbiProfile.AdobeFlashCs6Compatible)
    {
        var path = GetFixturePath("Phase6Core.class.as");
        var result = new Avm1CompilerService().Compile(
            new Avm1CompilationSource(path, File.ReadAllText(path)),
            new Avm1CompilerServiceOptions(swfVersion)
            {
                AbiProfile = abiProfile
            });
        result.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage} {diagnostic.Code}: " +
                diagnostic.Message)));
        return result.ProgramArtifact.ShouldNotBeNull().Classes
            .ShouldHaveSingleItem();
    }

    private static Avm1MethodCompatibilityInput ToCompatibilityInput(
        Avm1CompiledClassMember method,
        byte swfVersion) =>
        new(method.Declaration.Name, method.Method.Bytecode, swfVersion)
        {
            FunctionContext = new FunctionContext(
                method.FunctionFlags,
                method.Parameters),
            RegisterCount = method.RegisterCount
        };

    private static Dictionary<string, Avm1FunctionBody> ExtractFunctions(
        ShockwaveFlashFile swf)
    {
        var functions = swf.Tags
            .OfType<DoActionTag>()
            .SelectMany(tag => Avm1FunctionBodyExtractor.ExtractTopLevel(
                tag.Data,
                swf.Header.Version))
            .Where(function => function.Name.StartsWith(
                "phase6",
                StringComparison.Ordinal))
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        functions.Count.ShouldBe(ProbeCalls.Length);
        return functions;
    }

    private static ShockwaveFlashFile CreateCandidateSwf(
        byte swfVersion,
        IReadOnlyList<Avm1CompiledClassMember> methods)
    {
        var actions = new List<ShockwaveFlash.Avm1.Action>();
        foreach (var method in methods)
        {
            actions.Add(new ActionDefineFunction2(
                method.Declaration.Name,
                method.RegisterCount,
                method.FunctionFlags,
                method.Parameters,
                method.Method.Bytecode));
        }
        foreach (var call in ProbeCalls)
        {
            var values = new List<PushValue>
            {
                PushValue.String($"PHASE6A:{call.Name}=")
            };
            for (var i = call.Arguments.Length - 1; i >= 0; i--)
                values.Add(call.Arguments[i]);
            values.Add(PushValue.Integer(call.Arguments.Length));
            values.Add(PushValue.String(call.Name));
            actions.Add(new ActionPush(values));
            actions.Add(new ActionCallFunction());
            actions.Add(new ActionAdd2());
            actions.Add(new ActionTrace());
        }
        actions.Add(new ActionGetURL("FSCommand:quit", string.Empty));
        actions.Add(new ActionEnd());

        return new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                swfVersion,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 1000, 1000),
                FrameRate: Fixed8.FromSingle(12),
                FrameCount: 1),
            [
                new FileAttributesTag(
                    new TagMetadata(TagCode.FileAttributes, 0, 0),
                    (FileAttributesFlags)0),
                new DoActionTag(
                    new TagMetadata(TagCode.DoAction, 0, 0),
                    ShockwaveFlash.Avm1.Action.EncodeCollection(
                        actions,
                        swfVersion)),
                new ShowFrameTag(
                    new TagMetadata(TagCode.ShowFrame, 0, 0)),
                new EndTag(new TagMetadata(TagCode.End, 0, 0))
            ]);
    }

    private static ShockwaveFlashFile ReadOracleSwf() =>
        ShockwaveFlashFile.Disassemble(
            File.ReadAllBytes(GetOracleSwfPath()));

    private static string GetOracleSwfPath()
    {
        var configured = Environment.GetEnvironmentVariable(
            OracleOutputEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? GetFixturePath("Phase6Core.swf")
            : Path.GetFullPath(configured);
    }

    private static string GetFixturePath(string name) =>
        Path.Combine(
            AppContext.BaseDirectory,
            FixtureDirectory.Replace('/', Path.DirectorySeparatorChar),
            name);

    private static string? ResolveRufflePath()
    {
        var configured = Environment.GetEnvironmentVariable(
            "SHOCKWAVEFLASH_RUFFLE_PATH");
        var candidates = new[]
        {
            configured,
            OperatingSystem.IsWindows()
                ? @"C:\Program Files\ruffle\bin\ruffle.exe"
                : null
        };
        return candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static async Task<string> RunRuffleAsync(
        string rufflePath,
        string swfPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = rufflePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--no-gui");
        startInfo.ArgumentList.Add("--storage");
        startInfo.ArgumentList.Add("memory");
        startInfo.ArgumentList.Add("--dummy-external-interface");
        startInfo.ArgumentList.Add("--load-behavior");
        startInfo.ArgumentList.Add("blocking");
        startInfo.ArgumentList.Add("--max-execution-duration");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add(swfPath);
        startInfo.Environment["RUST_LOG"] = "info";

        using var process = new Process { StartInfo = startInfo };
        process.Start().ShouldBeTrue();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        return await stdout + Environment.NewLine + await stderr;
    }

    private static string Describe(Avm1MethodCompatibilityResult result) =>
        $"Rewrites: reference={result.SourceEquivalence?.ExpectedRewriteCount}, " +
        $"candidate={result.SourceEquivalence?.ActualRewriteCount}" +
        Environment.NewLine + string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Side} {diagnostic.Code}: " +
                diagnostic.Message)) +
        Environment.NewLine + "Reference Source:" + Environment.NewLine +
        (result.ReferenceSource?.GetAs2Text() ?? "<unavailable>") +
        FormatSymbols(result.ReferenceSource) +
        FormatNormalized("Reference", result.ReferenceSource) +
        Environment.NewLine + "Candidate Source:" + Environment.NewLine +
        (result.CandidateSource?.GetAs2Text() ?? "<unavailable>") +
        FormatSymbols(result.CandidateSource) +
        FormatNormalized("Candidate", result.CandidateSource);

    private static string FormatNormalized(
        string side,
        Avm1SourceMethod? method)
    {
        if (method is null)
            return string.Empty;

        var normalized = Avm1SourceNormalizer.Normalize(
            method,
            CancellationToken.None);
        return Environment.NewLine + side + " Normalized Source:" +
            Environment.NewLine + normalized.Method.GetAs2Text() +
            FormatSymbols(normalized.Method);
    }

    private static string FormatSymbols(Avm1SourceMethod? method) =>
        method is null
            ? string.Empty
            : Environment.NewLine + "Symbols: " + string.Join(
                ", ",
                method.Arena.Symbols.Select(symbol =>
                    $"{symbol.Kind}:{method.Arena[symbol.Name]}:{symbol.Flags}"));

    private static string FormatControlFlow(
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        FunctionContext context)
    {
        var core = Avm1Decompiler.BuildMethodCore(
            bytecode,
            swfVersion,
            context);
        var decompilation = Avm1Decompiler.DecompileMethod(core);
        var builder = new StringBuilder();
        foreach (var region in core.Instructions.TryRegions)
        {
            builder.Append("try h=b").Append(GetBlock(region.EnterAction))
                .Append(" body=b").Append(GetBlock(region.TryBodyStartAction))
                .Append("..b").Append(GetBlock(region.TryExitAction))
                .Append(" catch=b").Append(GetBlock(region.CatchEnterAction))
                .Append("..b").Append(GetBlock(region.CatchExitAction))
                .Append(" finally=b").Append(GetBlock(region.FinallyEnterAction))
                .Append("..b").Append(GetBlock(region.FinallyExitAction))
                .Append(" continuation=b").Append(GetBlock(region.ContinuationAction))
                .AppendLine();
        }
        foreach (var edge in core.LoopAnalysis.BackEdges)
        {
            builder.Append("back b").Append(edge.Tail.Value)
                .Append(" -> b").Append(edge.Header.Value)
                .AppendLine();
        }
        foreach (var loop in core.LoopAnalysis.Loops)
        {
            builder.Append("natural h=b").Append(loop.Header.Value)
                .Append(" tail=b").Append(loop.Tail.Value)
                .Append(" blocks=[")
                .AppendJoin(',', loop.Blocks.Select(block => block.Value))
                .Append("] exits=[")
                .AppendJoin(',', loop.Exits.Select(block => block.Value))
                .AppendLine("]");
        }
        foreach (var loop in core.RegionAnalysis.WhileRegions)
        {
            builder.Append("while h=b").Append(loop.Header.Value)
                .Append(" c=b").Append(loop.ConditionBlock.Value)
                .Append(" exit=b").Append(loop.Exit.Value)
                .Append(" blocks=[")
                .AppendJoin(',', loop.Blocks.Select(block => block.Value))
                .Append("] body=[")
                .AppendJoin(',', loop.BodyBlocks.Select(block => block.Value))
                .AppendLine("]");
        }
        foreach (var region in decompilation.SwitchAnalysis.Regions)
        {
            builder.Append("switch h=b").Append(region.Header.Value)
                .Append(" merge=b").Append(region.Merge.Value)
                .Append(" cases=").Append(region.Cases.Count)
                .Append(" stores=").Append(region.DispatchStores.Count)
                .AppendLine();
        }
        foreach (var block in core.ControlFlowGraph.Blocks)
        {
            builder.Append('b').Append(block.Index.Value)
                .Append(" a").Append(block.StartAction.Value)
                .Append("..").Append(block.EndAction.Value)
                .Append(' ').Append(block.Terminator)
                .Append(" -> b").Append(block.FirstSuccessor.Value)
                .Append(",b").Append(block.SecondSuccessor.Value)
                .Append(" ex=b").Append(block.ExceptionSuccessor.Value)
                .AppendLine();
        }
        return builder.ToString().TrimEnd();

        int GetBlock(ActionIndex action) =>
            action.IsValid && core.ControlFlowGraph.TryGetBlockForAction(action, out var block)
                ? block.Value
                : -1;
    }

    private sealed record ProbeCall(
        string Name,
        PushValue[] Arguments,
        string Expected,
        string? RuffleOracleExpected = null);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdobeFlashCs6Collection
{
    public const string Name = "Adobe Flash CS6";
}
