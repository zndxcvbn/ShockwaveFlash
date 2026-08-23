using System.Diagnostics;
using ShockwaveFlash;
using ShockwaveFlash.Avm1.Compatibility;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Decompilation;
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

[Collection(MtascCollection.Name)]
public sealed class Avm1MtascCompatibilityTests
{
    private const string OracleOutputEnvironmentVariable =
        "SHOCKWAVEFLASH_MTASC_ORACLE_OUTPUT";
    private const string CandidateOutputEnvironmentVariable =
        "SHOCKWAVEFLASH_MTASC_CANDIDATE_OUTPUT";
    private const string FixtureDirectory = "Avm1/Compatibility/Mtasc";

    private static readonly ProbeCall[] ProbeCalls =
    [
        new(
            "phase6gArithmetic",
            [PushValue.Integer(3), PushValue.Integer(4)],
            "14"),
        new("phase6gLoop", [PushValue.Integer(5)], "24"),
        new("phase6gClassify", [PushValue.Integer(2)], "small"),
        new("phase6gComputedUpdate", [PushValue.Integer(0)], "12"),
        new("phase6gLiterals", [PushValue.Integer(3)], "34"),
        new("phase6gShortCircuit", [PushValue.Integer(3)], "14"),
        new("phase6gClosure", [PushValue.Integer(3)], "7")
    ];

    private static readonly string[] ProbeMethodNames =
    [
        "phase6gArithmetic",
        "phase6gLoop",
        "phase6gClassify",
        "phase6gComputedUpdate",
        "phase6gLiterals",
        "phase6gShortCircuit",
        "phase6gClosure"
    ];

    [Fact]
    public void Checked_mtasc_oracle_projects_complete_source_methods()
    {
        var sourceClass = ReadOracleClass();

        sourceClass.IsComplete.ShouldBeTrue(Describe(sourceClass));
        sourceClass.Methods
            .Where(method => method.Name.StartsWith(
                "phase6g",
                StringComparison.Ordinal))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe(ProbeMethodNames.Order(StringComparer.Ordinal));
        sourceClass.Methods
            .Where(method => ProbeMethodNames.Contains(
                method.Name,
                StringComparer.Ordinal))
            .ShouldAllBe(method => method.Origin.Bytecode != null);
    }

    [Fact]
    public void Compiler_methods_are_source_compatible_with_the_mtasc_oracle()
    {
        var oracleClass = ReadOracleClass();
        var oracleMethods = oracleClass.Methods
            .Where(method => ProbeMethodNames.Contains(
                method.Name,
                StringComparer.Ordinal))
            .ToDictionary(method => method.Name, StringComparer.Ordinal);
        var candidate = CompileCandidate();
        var candidateMethods = candidate.Methods
            .Where(method => ProbeMethodNames.Contains(
                method.Declaration.Name,
                StringComparer.Ordinal))
            .ToDictionary(
                method => method.Declaration.Name,
                StringComparer.Ordinal);

        candidateMethods.Keys.Order(StringComparer.Ordinal).ShouldBe(
            oracleMethods.Keys.Order(StringComparer.Ordinal));

        var failures = new List<string>();
        foreach (var name in ProbeMethodNames)
        {
            var reference = ToCompatibilityInput(oracleMethods[name]);
            var compiled = ToCompatibilityInput(candidateMethods[name]);
            var result = Avm1MethodCompatibilityAnalyzer.Compare(
                reference,
                compiled);
            result.BytecodeIsValid.ShouldBeTrue(
                Describe(result) + Environment.NewLine +
                "MTASC P-code:" + Environment.NewLine +
                Avm1Disassembler.Disassemble(
                    reference.Bytecode,
                    reference.SwfVersion));
            if (!result.IsCompatible)
            {
                failures.Add(
                    name + Environment.NewLine +
                    Describe(result) + Environment.NewLine +
                    "MTASC P-code:" + Environment.NewLine +
                    Avm1Disassembler.Disassemble(
                        reference.Bytecode,
                        reference.SwfVersion) + Environment.NewLine +
                    "MTASC CFG:" + Environment.NewLine +
                    FormatControlFlow(reference) + Environment.NewLine +
                    "Candidate P-code:" + Environment.NewLine +
                    Avm1Disassembler.Disassemble(
                        compiled.Bytecode,
                        compiled.SwfVersion));
            }
        }

        failures.ShouldBeEmpty(string.Join(
            Environment.NewLine + Environment.NewLine,
            failures));
    }

    [Fact]
    public async Task Checked_mtasc_oracle_matches_a_live_compilation_when_available()
    {
        var sourcePath = GetFixturePath("Phase6Mtasc.as");
        var live = await MtascOracle.TryCompileAsync(
            await File.ReadAllTextAsync(sourcePath),
            "Phase6Mtasc.as",
            swfVersion: 7);
        if (live is null)
            return;

        var configuredOutput = Environment.GetEnvironmentVariable(
            OracleOutputEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredOutput))
        {
            var outputPath = Path.GetFullPath(configuredOutput);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(outputPath, live.Swf);
        }

        var checkedMethods = ReadOracleClass().Methods
            .Where(method => ProbeMethodNames.Contains(
                method.Name,
                StringComparer.Ordinal))
            .ToDictionary(method => method.Name, StringComparer.Ordinal);
        var liveClass = ReadOracleClass(live.Swf);
        liveClass.IsComplete.ShouldBeTrue(
            live.CompilerPath + Environment.NewLine +
            live.StandardOutput + Environment.NewLine +
            live.StandardError + Environment.NewLine +
            Describe(liveClass));
        var liveMethods = liveClass.Methods
            .Where(method => ProbeMethodNames.Contains(
                method.Name,
                StringComparer.Ordinal))
            .ToDictionary(method => method.Name, StringComparer.Ordinal);
        liveMethods.Keys.Order(StringComparer.Ordinal).ShouldBe(
            checkedMethods.Keys.Order(StringComparer.Ordinal));

        var failures = new List<string>();
        foreach (var name in ProbeMethodNames)
        {
            var result = Avm1MethodCompatibilityAnalyzer.Compare(
                ToCompatibilityInput(checkedMethods[name]),
                ToCompatibilityInput(liveMethods[name]));
            if (!result.IsCompatible)
                failures.Add(name + Environment.NewLine + Describe(result));
        }
        failures.ShouldBeEmpty(
            "MTASC: " + live.CompilerPath + Environment.NewLine +
            live.StandardOutput + Environment.NewLine +
            live.StandardError + Environment.NewLine +
            string.Join(
                Environment.NewLine + Environment.NewLine,
                failures));
    }

    [Fact]
    public async Task Compiler_methods_match_the_mtasc_runtime_markers()
    {
        var rufflePath = ResolveRufflePath();
        if (rufflePath is null)
            return;

        var candidate = CompileCandidate();
        var candidateSwf = CreateCandidateSwf(
            candidate.Methods
                .Where(method => ProbeMethodNames.Contains(
                    method.Declaration.Name,
                    StringComparer.Ordinal))
                .ToArray());
        var configuredOutput = Environment.GetEnvironmentVariable(
            CandidateOutputEnvironmentVariable);
        var candidatePath = string.IsNullOrWhiteSpace(configuredOutput)
            ? Path.Combine(
                Path.GetTempPath(),
                $"ShockwaveFlash-Phase6g-{Guid.NewGuid():N}.swf")
            : Path.GetFullPath(configuredOutput);
        var deleteCandidate = string.IsNullOrWhiteSpace(configuredOutput);
        try
        {
            var outputDirectory = Path.GetDirectoryName(candidatePath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(
                candidatePath,
                candidateSwf.Assemble().ToArray());
            var oracleOutput = await RunRuffleAsync(
                rufflePath,
                GetFixturePath("Phase6Mtasc.swf"));
            var candidateOutput = await RunRuffleAsync(
                rufflePath,
                candidatePath);
            foreach (var call in ProbeCalls)
            {
                var marker = $"PHASE6G:{call.Name}={call.Expected}";
                oracleOutput.Contains(marker, StringComparison.Ordinal)
                    .ShouldBeTrue(oracleOutput);
                candidateOutput.Contains(marker, StringComparison.Ordinal)
                    .ShouldBeTrue(candidateOutput);
            }
        }
        finally
        {
            if (deleteCandidate)
                File.Delete(candidatePath);
        }
    }

    private static Avm1SourceClass ReadOracleClass()
        => ReadOracleClass(File.ReadAllBytes(GetFixturePath("Phase6Mtasc.swf")));

    private static Avm1SourceClass ReadOracleClass(byte[] swfBytes)
    {
        var swf = ShockwaveFlashFile.Disassemble(
            swfBytes);
        Avm1ClassDecompiler.TryDecompileSourceFile(
            swf,
            "Phase6Mtasc",
            out var sourceFile).ShouldBeTrue();
        return sourceFile!.Classes.ShouldHaveSingleItem();
    }

    private static Avm1ClassArtifact CompileCandidate()
    {
        var path = GetFixturePath("Phase6Mtasc.as");
        var result = new Avm1CompilerService().Compile(
            new Avm1CompilationSource(path, File.ReadAllText(path)),
            new Avm1CompilerServiceOptions(7));
        result.Succeeded.ShouldBeTrue(string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Stage} {diagnostic.Code}: " +
                diagnostic.Message)));
        return result.ProgramArtifact.ShouldNotBeNull().Classes
            .ShouldHaveSingleItem();
    }

    private static ShockwaveFlashFile CreateCandidateSwf(
        IReadOnlyList<Avm1CompiledClassMember> methods)
    {
        const byte swfVersion = 7;
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
                PushValue.String($"PHASE6G:{call.Name}=")
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

    private static Avm1MethodCompatibilityInput ToCompatibilityInput(
        Avm1SourceMethodDeclaration method)
    {
        var origin = method.Origin.Bytecode.ShouldNotBeNull();
        return new Avm1MethodCompatibilityInput(
            method.Name,
            origin.Body,
            origin.SwfVersion)
        {
            FunctionContext = new FunctionContext(
                (FunctionFlags)origin.FunctionFlags,
                origin.Parameters.Select(parameter =>
                    new FunctionParameter(
                        parameter.Register,
                        parameter.Name)).ToArray()),
            RegisterCount = origin.RegisterCount,
            InitialConstantPool = origin.InitialConstantPool
        };
    }

    private static Avm1MethodCompatibilityInput ToCompatibilityInput(
        Avm1CompiledClassMember method) =>
        new(method.Declaration.Name, method.Method.Bytecode, swfVersion: 7)
        {
            FunctionContext = new FunctionContext(
                method.FunctionFlags,
                method.Parameters),
            RegisterCount = method.RegisterCount
        };

    private static string Describe(Avm1MethodCompatibilityResult result) =>
        $"Rewrites: reference={result.SourceEquivalence?.ExpectedRewriteCount}, " +
        $"candidate={result.SourceEquivalence?.ActualRewriteCount}" +
        Environment.NewLine + string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Side} {diagnostic.Code}: " +
                diagnostic.Message)) +
        Environment.NewLine + "MTASC Source:" + Environment.NewLine +
        (result.ReferenceSource?.GetAs2Text() ?? "<unavailable>") +
        FormatSymbols(result.ReferenceSource) +
        FormatNormalized("MTASC", result.ReferenceSource) +
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
            FormatSymbols(normalized.Method) +
            FormatTopLevel(normalized.Method);
    }

    private static string FormatSymbols(Avm1SourceMethod? method) =>
        method is null
            ? string.Empty
            : Environment.NewLine + "Symbols: " + string.Join(
                ", ",
                method.Arena.Symbols.Select(symbol =>
                    $"{symbol.Kind}:{method.Arena[symbol.Name]}:{symbol.Flags}"));

    private static string FormatTopLevel(Avm1SourceMethod method)
    {
        var arena = method.Arena;
        var body = arena[method.Body];
        var lines = new List<string>();
        for (var i = 0; i < body.Children.Count; i++)
        {
            var statement = arena[arena.GetChild(body, i)];
            var symbol = statement.Symbol.IsValid
                ? arena[arena[statement.Symbol].Name]
                : "-";
            var expression = statement.Expression.IsValid
                ? arena[statement.Expression]
                : default;
            var children = statement.Expression.IsValid
                ? string.Join(
                    ",",
                    Enumerable.Range(0, expression.Children.Count).Select(childIndex =>
                    {
                        var child = arena[arena.GetChild(expression, childIndex)];
                        return child.Kind is Avm1SourceExpressionKind.SymbolReference &&
                            child.Symbol.IsValid
                                ? arena[arena[child.Symbol].Name]
                                : child.Kind.ToString();
                    }))
                : string.Empty;
            lines.Add(
                $"[{i}] {statement.Kind}:{symbol} " +
                $"{expression.Kind}({children})");
        }
        return Environment.NewLine + "Top level: " + string.Join("; ", lines);
    }

    private static string Describe(Avm1SourceClass sourceClass) =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            sourceClass.Methods
                .Where(method => !method.IsComplete)
                .Select(method =>
                    method.Name + Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        method.Body.Diagnostics.Select(diagnostic =>
                            $"{diagnostic.Severity} {diagnostic.Code}: " +
                            diagnostic.Message)) + Environment.NewLine +
                    method.Body.GetAs2Text()));

    private static string FormatControlFlow(
        Avm1MethodCompatibilityInput input)
    {
        var core = Avm1Decompiler.BuildMethodCore(
            input.Bytecode,
            input.SwfVersion,
            input.FunctionContext);
        var builder = new System.Text.StringBuilder();
        foreach (var loop in core.LoopAnalysis.Loops)
        {
            builder.Append("natural h=b").Append(loop.Header.Value)
                .Append(" tail=b").Append(loop.Tail.Value)
                .Append(" blocks=[")
                .AppendJoin(',', loop.Blocks.Select(block => block.Value))
                .AppendLine("]");
        }
        foreach (var loop in core.RegionAnalysis.WhileRegions)
        {
            builder.Append("while h=b").Append(loop.Header.Value)
                .Append(" c=b").Append(loop.ConditionBlock.Value)
                .Append(" body=b").Append(loop.BodyEntry.Value)
                .Append(" exit=b").Append(loop.Exit.Value)
                .Append(" blocks=[")
                .AppendJoin(',', loop.Blocks.Select(block => block.Value))
                .Append("] bodyBlocks=[")
                .AppendJoin(',', loop.BodyBlocks.Select(block => block.Value))
                .AppendLine("]");
        }
        foreach (var block in core.ControlFlowGraph.Blocks)
        {
            builder.Append('b').Append(block.Index.Value)
                .Append(" a").Append(block.StartAction.Value)
                .Append("..").Append(block.EndAction.Value)
                .Append(' ').Append(block.Terminator)
                .Append(" -> b").Append(block.FirstSuccessor.Value)
                .Append(",b").Append(block.SecondSuccessor.Value)
                .AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

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

    private static string GetFixturePath(string name) =>
        Path.Combine(
            AppContext.BaseDirectory,
            FixtureDirectory.Replace('/', Path.DirectorySeparatorChar),
            name);

    private sealed record ProbeCall(
        string Name,
        PushValue[] Arguments,
        string Expected);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MtascCollection
{
    public const string Name = "MTASC";
}
