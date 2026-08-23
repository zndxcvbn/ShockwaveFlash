using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShockwaveFlash.Avm1;
using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Avm1.Compatibility;
using ShockwaveFlash.Avm1.Decompilation;
using ShockwaveFlash.Avm1.Decompilation.Analysis;
using ShockwaveFlash.Avm1.Decompilation.Ast;
using ShockwaveFlash.Avm1.Decompilation.Ir;
using ShockwaveFlash.Avm1.Decompilation.Ssa;
using ShockwaveFlash.Avm1.Special;
using ShockwaveFlash.Avm1.Swf4;
using ShockwaveFlash.Avm1.Swf5;
using ShockwaveFlash.Avm1.Swf7;
using ShockwaveFlash.Avm1.Source;
using ShockwaveFlash.Avm1.Text;
using ShockwaveFlash.Avm1.Types;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Action;
using ShockwaveFlash.Tags.Button;
using ShockwaveFlash.Tags.DisplayList;
using ShockwaveFlash.Tags.Metadata;
using ShockwaveFlash.Tags.Sprite;
using ShockwaveFlash.Types.DisplayList;
using Shouldly;
using Avm1Action = ShockwaveFlash.Avm1.Action;

namespace ShockwaveFlash.Tests;

public sealed class Avm1CorpusAuditTests
{
    private const string ExtraCorpusEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_EXTRA_CORPUS";
    private const string ReportEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_CORPUS_REPORT";
    private const string FilterEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_CORPUS_FILTER";

    [Fact]
    public void Repository_avm1_corpus_strictly_decodes_and_projects_without_crashing()
    {
        var files = CollectCorpusFiles();
        if (files.Count == 0)
            return;

        var reportPath = GetReportPath();
        var progressPath = Environment.GetEnvironmentVariable(
            ReportEnvironmentVariable) is null
                ? null
                : reportPath + ".progress.json";
        var report = Avm1CorpusAuditor.Audit(
            files,
            progressPath is null
                ? null
                : progress => WriteProgress(progressPath, progress));
        WriteReport(reportPath, report);

        report.Files.Count.ShouldBe(files.Count);
        report.ActionBlobCount.ShouldBeGreaterThan(0);
        report.CodeUnitCount.ShouldBeGreaterThan(0);
        report.Failures.ShouldBeEmpty(report.FormatSummary());
    }

    private static IReadOnlyList<string> CollectCorpusFiles()
    {
        var files = new HashSet<string>(Corpus.Files(), StringComparer.OrdinalIgnoreCase);
        var filter = Environment.GetEnvironmentVariable(FilterEnvironmentVariable);
        var extra = Environment.GetEnvironmentVariable(ExtraCorpusEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var candidate in extra.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var path = Path.GetFullPath(candidate);
                if (File.Exists(path) &&
                    string.Equals(Path.GetExtension(path), ".swf", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(path);
                    continue;
                }

                if (!Directory.Exists(path))
                    continue;
                foreach (var file in Directory.EnumerateFiles(
                    path,
                    "*.swf",
                    SearchOption.AllDirectories))
                {
                    files.Add(Path.GetFullPath(file));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            files.RemoveWhere(path =>
                !path.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static string GetReportPath()
    {
        var path = Environment.GetEnvironmentVariable(ReportEnvironmentVariable) ??
            Path.Combine(
                Path.GetTempPath(),
                "ShockwaveFlash.Tests",
                "avm1-corpus-audit.json");

        return Path.GetFullPath(path);
    }

    private static void WriteReport(
        string path,
        Avm1CorpusAuditReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteProgress(
        string path,
        Avm1CorpusAuditProgress progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(
                progress,
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }
}

internal readonly record struct Avm1CorpusAuditProgress(
    int TotalFiles,
    int CompletedFiles,
    string? CurrentFile,
    bool IsCompleted,
    long ElapsedMilliseconds);

internal static class Avm1CorpusAuditor
{
    private const string SkipSourceProjectionEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_SKIP_SOURCE_PROJECTION";
    private const string CodeUnitFilterEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_CODE_UNIT_FILTER";
    private const string PipelineStageEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_PIPELINE_STAGE";
    private const string CompileRoundTripEnvironmentVariable =
        "SHOCKWAVEFLASH_AVM1_COMPILE_ROUND_TRIP";

    private static readonly Regex RawTemporaryPattern = new(
        @"\bvar\s+(v[0-9]+)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex TemporaryReferencePattern = new(
        @"\bv[0-9]+\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex SelfInitializingTemporaryPattern = new(
        @"\bvar\s+(v[0-9]+)\s*=\s*\1\s*;",
        RegexOptions.CultureInvariant);
    private static readonly Regex EmptyIfPattern = new(
        @"\bif\s*\([^\r\n]*\)\s*\r?\n\s*\{\s*\r?\n\s*\}",
        RegexOptions.CultureInvariant);
    private static readonly Regex VersionedRegisterPattern = new(
        @"\b_loc[0-9]+_v[0-9]+\b",
        RegexOptions.CultureInvariant);

    public static Avm1CorpusAuditReport Audit(
        IReadOnlyList<string> paths,
        Action<Avm1CorpusAuditProgress>? reportProgress = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var stopwatch = Stopwatch.StartNew();
        var report = new Avm1CorpusAuditReport();
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            reportProgress?.Invoke(new Avm1CorpusAuditProgress(
                paths.Count,
                index,
                path,
                IsCompleted: false,
                stopwatch.ElapsedMilliseconds));
            AuditFile(Path.GetFullPath(path), report);
        }
        report.Files.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Path, right.Path));
        report.Failures.Sort(StringComparer.Ordinal);
        stopwatch.Stop();
        reportProgress?.Invoke(new Avm1CorpusAuditProgress(
            paths.Count,
            paths.Count,
            CurrentFile: null,
            IsCompleted: true,
            stopwatch.ElapsedMilliseconds));
        return report;
    }

    private static void AuditFile(string path, Avm1CorpusAuditReport report)
    {
        var result = new Avm1CorpusFileAudit { Path = path };
        report.Files.Add(result);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var bytes = File.ReadAllBytes(path);
            result.ByteLength = bytes.Length;
            var swf = ShockwaveFlashFile.Disassemble(bytes);
            result.SwfVersion = swf.Header.Version;
            result.IsActionScript3 = swf.Tags
                .OfType<FileAttributesTag>()
                .Any(tag => tag.IsActionScript3);

            var blobs = new List<Avm1ActionBlob>();
            CollectActionBlobs(swf.Tags, path, swf.Header.Version, blobs);
            result.ActionBlobCount = blobs.Count;
            report.ActionBlobCount += blobs.Count;
            result.ContainsMalformedCode = blobs.Any(BlobContainsMalformedCode);
            if (result.ContainsMalformedCode)
                report.MalformedFileCount++;
            foreach (var blob in blobs)
                AuditBlob(blob, report, result);

            AuditClasses(swf, report, result);
        }
        catch (Exception exception)
        {
            AddFailure(report, result, path, "SWF", exception);
        }
        finally
        {
            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        }
    }

    private static void AuditBlob(
        Avm1ActionBlob blob,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        if (!TryDecodeActions(
                blob.Path,
                blob.Bytecode,
                blob.SwfVersion,
                report,
                file,
                out var actions,
                out var strictSucceeded))
        {
            return;
        }

        RecordCodecResult(
            blob.Bytecode,
            actions,
            blob.SwfVersion,
            strictSucceeded,
            report,
            file);

        AuditCodeUnit(
            blob.Path,
            blob.Bytecode,
            actions,
            blob.SwfVersion,
            context: null,
            registerCount: null,
            initialConstantPool: null,
            report,
            file);
        VisitNestedFunctions(
            blob.Path,
            actions,
            blob.SwfVersion,
            inheritedConstantPool: null,
            report,
            file);
    }

    private static bool BlobContainsMalformedCode(Avm1ActionBlob blob)
    {
        try
        {
            var actions = Avm1Action.DecodeCollection(
                blob.Bytecode,
                blob.SwfVersion,
                Avm1ActionDecodeMode.RecoverMalformed);
            return actions.Any(action => action is ActionMalformed) ||
                ContainsMalformedDescendant(actions, blob.SwfVersion);
        }
        catch
        {
            return true;
        }
    }

    private static void AuditCodeUnit(
        string path,
        ReadOnlyMemory<byte> bytecode,
        IReadOnlyList<Avm1Action> actions,
        byte swfVersion,
        FunctionContext? context,
        byte? registerCount,
        IReadOnlyList<string>? initialConstantPool,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        try
        {
            report.CodeUnitCount++;
            file.CodeUnitCount++;
            if (actions.Count > report.MaximumCodeUnitActionCount)
            {
                report.MaximumCodeUnitActionCount = actions.Count;
                report.MaximumCodeUnitPath = path;
            }
            if (actions.Count > file.MaximumCodeUnitActionCount)
            {
                file.MaximumCodeUnitActionCount = actions.Count;
                file.MaximumCodeUnitPath = path;
            }

            var malformedActions = actions.Count(action =>
                action is ActionMalformed);
            report.MalformedActionCount += malformedActions;
            file.MalformedActionCount += malformedActions;
            if (malformedActions > 0 ||
                ContainsMalformedDescendant(actions, swfVersion))
            {
                report.SkippedMalformedProjectionCount++;
                file.SkippedMalformedProjectionCount++;
                report.ActionCount += actions.Count;
                file.ActionCount += actions.Count;
                return;
            }

            var codeUnitFilter = Environment.GetEnvironmentVariable(
                CodeUnitFilterEnvironmentVariable);
            if ((!string.IsNullOrWhiteSpace(codeUnitFilter) &&
                    !path.Contains(
                        codeUnitFilter,
                        StringComparison.OrdinalIgnoreCase)) ||
                (file.ContainsMalformedCode &&
                    string.IsNullOrWhiteSpace(codeUnitFilter)))
            {
                report.SkippedSourceProjectionCount++;
                file.SkippedSourceProjectionCount++;
                report.ActionCount += actions.Count;
                file.ActionCount += actions.Count;
                return;
            }

            if (string.Equals(
                    Environment.GetEnvironmentVariable(
                        SkipSourceProjectionEnvironmentVariable),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                report.SkippedSourceProjectionCount++;
                file.SkippedSourceProjectionCount++;
                report.ActionCount += actions.Count;
                file.ActionCount += actions.Count;
                return;
            }

            IReadOnlyList<Avm1Action> contextualActions = actions;
            if (initialConstantPool is not null)
            {
                var values = new List<Avm1Action>(actions.Count + 1)
                {
                    new ActionConstantPool(initialConstantPool)
                };
                values.AddRange(actions);
                contextualActions = values;
            }

            var pipelineStage = Environment.GetEnvironmentVariable(
                PipelineStageEnvironmentVariable);
            if (string.Equals(
                    pipelineStage,
                    "core-details",
                    StringComparison.OrdinalIgnoreCase))
            {
                var coreAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
                var coreStopwatch = Stopwatch.StartNew();
                var auditCore = AuditCoreStages(
                    contextualActions,
                    swfVersion,
                    context,
                    report);
                coreStopwatch.Stop();
                var coreAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) -
                    coreAllocationStart;
                report.CoreAuditCount++;
                report.CoreAuditMilliseconds += coreStopwatch.ElapsedMilliseconds;
                report.CoreAuditAllocatedBytes += coreAllocatedBytes;
                if (auditCore.ControlFlowGraph.Count > report.MaximumCodeUnitBlockCount)
                {
                    report.MaximumCodeUnitBlockCount = auditCore.ControlFlowGraph.Count;
                    report.MaximumCodeUnitBlockPath = path;
                }
                report.ActionCount += auditCore.Instructions.Count;
                file.ActionCount += auditCore.Instructions.Count;
                return;
            }

            if (string.Equals(
                    pipelineStage,
                    "core",
                    StringComparison.OrdinalIgnoreCase))
            {
                var coreAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
                var coreStopwatch = Stopwatch.StartNew();
                var auditCore = Avm1Decompiler.BuildMethodCore(
                    contextualActions,
                    swfVersion,
                    context);
                coreStopwatch.Stop();
                var coreAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) -
                    coreAllocationStart;
                report.CoreAuditCount++;
                report.CoreAuditMilliseconds += coreStopwatch.ElapsedMilliseconds;
                report.CoreAuditAllocatedBytes += coreAllocatedBytes;
                if (auditCore.ControlFlowGraph.Count > report.MaximumCodeUnitBlockCount)
                {
                    report.MaximumCodeUnitBlockCount = auditCore.ControlFlowGraph.Count;
                    report.MaximumCodeUnitBlockPath = path;
                }
                report.ActionCount += auditCore.Instructions.Count;
                file.ActionCount += auditCore.Instructions.Count;
                return;
            }

            var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
            var projectionStopwatch = Stopwatch.StartNew();

            var stageAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
            var stageStopwatch = Stopwatch.StartNew();
            var core = Avm1Decompiler.BuildMethodCore(
                contextualActions,
                swfVersion,
                context);
            stageStopwatch.Stop();
            report.CoreAnalysisMilliseconds += stageStopwatch.ElapsedMilliseconds;
            report.CoreAnalysisAllocatedBytes +=
                GC.GetTotalAllocatedBytes(precise: false) - stageAllocationStart;

            stageAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
            stageStopwatch.Restart();
            var valueAnalysis = Avm1ValueAnalysis.Build(
                core.Instructions,
                core.TacIr,
                core.RegisterSsa,
                core.ValueOrigins,
                core.CatchPayloads,
                core.ConstantPools);
            stageStopwatch.Stop();
            report.ValueAnalysisMilliseconds += stageStopwatch.ElapsedMilliseconds;
            report.ValueAnalysisAllocatedBytes +=
                GC.GetTotalAllocatedBytes(precise: false) - stageAllocationStart;

            stageAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
            stageStopwatch.Restart();
            var switchAnalysis = Avm1SwitchAnalysis.Build(
                core.ControlFlowGraph,
                core.TacIr,
                core.RegisterSsa,
                valueAnalysis,
                core.RegionAnalysis);
            stageStopwatch.Stop();
            report.SwitchAnalysisMilliseconds += stageStopwatch.ElapsedMilliseconds;
            report.SwitchAnalysisAllocatedBytes +=
                GC.GetTotalAllocatedBytes(precise: false) - stageAllocationStart;

            stageAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
            stageStopwatch.Restart();
            var scopeAnalysis = Avm1ScopeAnalysis.Run(
                core.TacIr,
                core.RegisterSsa,
                valueAnalysis,
                core.ValueOrigins,
                core.Context);
            stageStopwatch.Stop();
            report.ScopeAnalysisMilliseconds += stageStopwatch.ElapsedMilliseconds;
            report.ScopeAnalysisAllocatedBytes +=
                GC.GetTotalAllocatedBytes(precise: false) - stageAllocationStart;

            stageAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
            stageStopwatch.Restart();
            var structuredAst = Avm1AstBuilder.Build(
                core.ControlFlowGraph,
                core.Instructions,
                core.TacIr,
                valueAnalysis,
                core.RegisterSsa,
                core.RegionAnalysis,
                switchAnalysis,
                scopeAnalysis.SymbolTable,
                reachability: core.Reachability);
            stageStopwatch.Stop();
            report.AstRecoveryMilliseconds += stageStopwatch.ElapsedMilliseconds;
            report.AstRecoveryAllocatedBytes +=
                GC.GetTotalAllocatedBytes(precise: false) - stageAllocationStart;

            var method = new Avm1MethodDecompilation(
                core,
                valueAnalysis,
                switchAnalysis,
                scopeAnalysis.SymbolTable,
                structuredAst,
                typeEnvironment: null,
                timelineLayout: null);

            stageAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
            stageStopwatch.Restart();
            var source = method.ProjectSource();
            stageStopwatch.Stop();
            report.SourceModelProjectionMilliseconds += stageStopwatch.ElapsedMilliseconds;
            report.SourceModelProjectionAllocatedBytes +=
                GC.GetTotalAllocatedBytes(precise: false) - stageAllocationStart;
            report.SourceExpressionCount += source.Arena.Expressions.Count;
            report.SourceStatementCount += source.Arena.Statements.Count;
            report.SourceSymbolCount += source.Arena.Symbols.Count;
            report.SourceOriginCount += source.Arena.Origins.Count;
            if (source.Arena.Expressions.Count > report.MaximumSourceExpressionCount)
            {
                report.MaximumSourceExpressionCount = source.Arena.Expressions.Count;
                report.MaximumSourceStatementCount = source.Arena.Statements.Count;
                report.MaximumSourceSymbolCount = source.Arena.Symbols.Count;
                report.MaximumSourceOriginCount = source.Arena.Origins.Count;
                report.MaximumSourceArenaPath = path;
            }

            stageAllocationStart = GC.GetTotalAllocatedBytes(precise: false);
            stageStopwatch.Restart();
            var text = source.GetAs2Text();
            stageStopwatch.Stop();
            report.SourceEmissionMilliseconds += stageStopwatch.ElapsedMilliseconds;
            report.SourceEmissionAllocatedBytes +=
                GC.GetTotalAllocatedBytes(precise: false) - stageAllocationStart;

            projectionStopwatch.Stop();
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) -
                allocationStart;
            report.SourceProjectionCount++;
            report.SourceProjectionMilliseconds += projectionStopwatch.ElapsedMilliseconds;
            report.SourceProjectionAllocatedBytes += allocatedBytes;
            file.SourceProjectionCount++;
            file.SourceProjectionMilliseconds += projectionStopwatch.ElapsedMilliseconds;
            file.SourceProjectionAllocatedBytes += allocatedBytes;
            if (projectionStopwatch.ElapsedMilliseconds >
                report.MaximumSourceProjectionMilliseconds)
            {
                report.MaximumSourceProjectionMilliseconds =
                    projectionStopwatch.ElapsedMilliseconds;
                report.MaximumSourceProjectionAllocatedBytes = allocatedBytes;
                report.MaximumSourceProjectionPath = path;
            }
            report.ActionCount += method.Instructions.Count;
            file.ActionCount += method.Instructions.Count;
            var unknownActions = method.StackIr.Instructions.Count(instruction =>
                instruction.Op is Avm1StackIrOp.UnknownAction);
            report.UnknownActionCount += unknownActions;
            file.UnknownActionCount += unknownActions;
            foreach (var instruction in method.StackIr.Instructions.Where(instruction =>
                instruction.Op is Avm1StackIrOp.UnknownAction))
            {
                var opcode = method.Instructions[instruction.Action].Action.Opcode.ToString();
                Increment(report.UnknownOpcodes, opcode);
            }

            var stackDiagnostics = method.StackIr.Diagnostics.Count +
                method.StackDepthAnalysis.Diagnostics.Count;
            report.StackDiagnosticCount += stackDiagnostics;
            file.StackDiagnosticCount += stackDiagnostics;
            foreach (var diagnostic in method.StackIr.Diagnostics)
            {
                Increment(report.StackDiagnostics, NormalizeStackDiagnostic(diagnostic.Message));
                RecordStackErrorLocation(report, path, method, diagnostic);
            }
            foreach (var diagnostic in method.StackDepthAnalysis.Diagnostics)
            {
                Increment(report.StackDiagnostics, NormalizeStackDiagnostic(diagnostic.Message));
                RecordStackErrorLocation(report, path, method, diagnostic);
            }
            if (method.IrreducibleControlFlow.IsIrreducible)
            {
                report.IrreducibleCodeUnitCount++;
                file.IrreducibleCodeUnitCount++;
            }

            report.OpaqueRegionCount += source.Arena.OpaqueRegions.Count;
            file.OpaqueRegionCount += source.Arena.OpaqueRegions.Count;
            foreach (var diagnostic in source.Diagnostics)
            {
                var counts = diagnostic.Severity switch
                {
                    Avm1SourceDiagnosticSeverity.Error => report.SourceErrors,
                    Avm1SourceDiagnosticSeverity.Warning => report.SourceWarnings,
                    _ => report.SourceInformation
                };
                Increment(counts, diagnostic.Code);
                if (diagnostic.Severity is Avm1SourceDiagnosticSeverity.Error)
                {
                    report.SourceErrorCount++;
                    file.SourceErrorCount++;
                    if (report.SourceErrorLocations.Count < 100)
                    {
                        report.SourceErrorLocations.Add(
                            $"{path}: {diagnostic.Code}: {diagnostic.Message}");
                    }
                }
                else if (diagnostic.Severity is Avm1SourceDiagnosticSeverity.Warning)
                {
                    report.SourceWarningCount++;
                    file.SourceWarningCount++;
                }
            }

            if (string.Equals(
                    Environment.GetEnvironmentVariable(
                        CompileRoundTripEnvironmentVariable),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                AuditRoundTrip(
                    path,
                    bytecode,
                    swfVersion,
                    context,
                    registerCount,
                    initialConstantPool,
                    report,
                    file);
            }

            var rawTemporaries = RawTemporaryPattern.Matches(text).Count;
            var temporaryReferences = TemporaryReferencePattern.Matches(text).Count;
            var versionedRegisters = VersionedRegisterPattern.Matches(text).Count;
            var declaredTemporaries = RawTemporaryPattern.Matches(text)
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            var unboundTemporaries = TemporaryReferencePattern.Matches(text)
                .Select(match => match.Value)
                .Where(name => !declaredTemporaries.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var selfInitializingTemporaries =
                SelfInitializingTemporaryPattern.Matches(text).Count;
            var emptyIfStatements = EmptyIfPattern.Matches(text).Count;
            report.RawTemporaryCount += rawTemporaries;
            file.RawTemporaryCount += rawTemporaries;
            report.TemporaryReferenceCount += temporaryReferences;
            file.TemporaryReferenceCount += temporaryReferences;
            report.VersionedRegisterCount += versionedRegisters;
            file.VersionedRegisterCount += versionedRegisters;
            report.UnboundTemporaryCount += unboundTemporaries.Length;
            file.UnboundTemporaryCount += unboundTemporaries.Length;
            report.SelfInitializingTemporaryCount += selfInitializingTemporaries;
            file.SelfInitializingTemporaryCount += selfInitializingTemporaries;
            report.EmptyIfStatementCount += emptyIfStatements;
            file.EmptyIfStatementCount += emptyIfStatements;
            if (rawTemporaries > 0 && report.RawTemporaryLocations.Count < 100)
                report.RawTemporaryLocations.Add(path);
            if (rawTemporaries > 0 && report.RawTemporaryDetails.Count < 100)
            {
                report.RawTemporaryDetails.Add(
                    $"{path}: definitions: " +
                    $"{GetTemporaryDefinitions(method, declaredTemporaries)}; " +
                    $"context: {GetTemporarySurroundingContext(text, declaredTemporaries)}");
            }
            if (temporaryReferences > 0 &&
                report.TemporaryReferenceLocations.Count < 100)
            {
                report.TemporaryReferenceLocations.Add(path);
            }
            if (versionedRegisters > 0 && report.VersionedRegisterLocations.Count < 100)
                report.VersionedRegisterLocations.Add(path);
            if (unboundTemporaries.Length > 0 &&
                report.UnboundTemporaryLocations.Count < 100)
            {
                report.UnboundTemporaryLocations.Add(
                    $"{path}: {string.Join(", ", unboundTemporaries)}; " +
                    $"definitions: {GetTemporaryDefinitions(method, unboundTemporaries)}; " +
                    $"context: {GetTemporaryReferenceContext(text, unboundTemporaries)}");
            }
            if (selfInitializingTemporaries > 0 &&
                report.SelfInitializingTemporaryLocations.Count < 100)
            {
                report.SelfInitializingTemporaryLocations.Add(path);
            }
            if (emptyIfStatements > 0 && report.EmptyIfStatementLocations.Count < 100)
                report.EmptyIfStatementLocations.Add(path);
        }
        catch (Exception exception)
        {
            AddFailure(report, file, path, "method projection", exception);
        }
    }

    private static Avm1MethodCore AuditCoreStages(
        IReadOnlyList<Avm1Action> actions,
        byte swfVersion,
        FunctionContext? context,
        Avm1CorpusAuditReport report)
    {
        T Measure<T>(string name, Func<T> build)
        {
            var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
            var stopwatch = Stopwatch.StartNew();
            var result = build();
            stopwatch.Stop();
            report.CoreStageMilliseconds[name] =
                report.CoreStageMilliseconds.GetValueOrDefault(name) +
                stopwatch.ElapsedMilliseconds;
            report.CoreStageAllocatedBytes[name] =
                report.CoreStageAllocatedBytes.GetValueOrDefault(name) +
                GC.GetTotalAllocatedBytes(precise: false) - allocationStart;
            return result;
        }

        var rootBytecode = Measure(
            "ActionEncoding",
            () => Avm1Action.EncodeCollection(actions, swfVersion));
        var instructions = Measure(
            "InstructionTable",
            () => Avm1InstructionTable.Build(actions, rootBytecode, swfVersion));
        var cfg = Measure(
            "ControlFlowGraph",
            () => Avm1ControlFlowGraph.Build(instructions));
        var flowGraph = Measure(
            "FlowGraph",
            () => Avm1FlowGraph.Build(instructions, cfg));
        var reachability = Measure(
            "Reachability",
            () => Avm1ReachabilityAnalysis.Build(cfg, flowGraph));
        var irreducibleControlFlow = Measure(
            "IrreducibleControlFlow",
            () => Avm1IrreducibleControlFlowAnalysis.Build(cfg));
        var dominatorTree = Measure(
            "DominatorTree",
            () => Avm1DominatorTree.Build(cfg));
        var postDominatorTree = Measure(
            "PostDominatorTree",
            () => Avm1PostDominatorTree.Build(cfg));
        var loopAnalysis = Measure(
            "LoopAnalysis",
            () => Avm1LoopAnalysis.Build(cfg, dominatorTree));
        var regionAnalysis = Measure(
            "RegionAnalysis",
            () => Avm1RegionAnalysis.Build(
                instructions,
                cfg,
                postDominatorTree,
                loopAnalysis));
        var stackDepthAnalysis = Measure(
            "StackDepthAnalysis",
            () => Avm1StackDepthAnalysis.Build(instructions, cfg, flowGraph));
        var linearStackIr = Measure(
            "LinearStackIr",
            () => Avm1StackIr.Build(instructions));
        var stackIr = Measure(
            "StackSsa",
            () => Avm1StackSsa.Build(
                instructions,
                cfg,
                flowGraph,
                stackDepthAnalysis,
                linearStackIr,
                takeLinearOwnership: true));
        var tacIr = Measure(
            "TacIr",
            () => Avm1TacIr.Build(instructions, stackIr));
        var completionSsa = Measure(
            "CompletionSsa",
            () => Avm1CompletionSsa.Build(cfg, tacIr, flowGraph));
        var catchPayloads = Measure(
            "CatchPayloadAnalysis",
            () => Avm1CatchPayloadAnalysis.Build(instructions, cfg, completionSsa));
        var registerSsa = Measure(
            "RegisterSsa",
            () => Avm1RegisterSsa.Build(
                tacIr,
                cfg,
                flowGraph,
                catchPayloads));
        var constantPools = Measure(
            "ConstantPoolAnalysis",
            () => Avm1ConstantPoolAnalysis.Build(instructions, cfg));
        var valueOrigins = Measure(
            "ValueOriginAnalysis",
            () => Avm1ValueOriginAnalysis.Build(
                instructions,
                tacIr,
                registerSsa,
                catchPayloads,
                constantPools,
                context));

        return Measure(
            "MethodCore",
            () => new Avm1MethodCore(
                instructions,
                cfg,
                flowGraph,
                reachability,
                irreducibleControlFlow,
                dominatorTree,
                postDominatorTree,
                regionAnalysis,
                loopAnalysis,
                stackDepthAnalysis,
                stackIr,
                tacIr,
                completionSsa,
                catchPayloads,
                registerSsa,
                constantPools,
                valueOrigins,
                context));
    }

    private static void AuditRoundTrip(
        string path,
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        FunctionContext? context,
        byte? registerCount,
        IReadOnlyList<string>? initialConstantPool,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        report.RoundTripAttemptCount++;
        file.RoundTripAttemptCount++;
        var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = Avm1RecoveredMethodRoundTripAnalyzer.Analyze(
                new Avm1MethodCompatibilityInput(path, bytecode, swfVersion)
                {
                    FunctionContext = context,
                    RegisterCount = registerCount,
                    InitialConstantPool = initialConstantPool
                });
            var status = result.Status.ToString();
            Increment(report.RoundTripStatuses, status);
            Increment(file.RoundTripStatuses, status);
            foreach (var diagnostic in result.Diagnostics)
            {
                Increment(
                    report.RoundTripDiagnostics,
                    $"{diagnostic.Stage}:{diagnostic.Code}");
            }
            if (result.Compatibility?.SourceEquivalence is { } equivalence)
            {
                foreach (var diagnostic in equivalence.Diagnostics)
                {
                    Increment(
                        report.RoundTripMismatches,
                        ClassifyRoundTripMismatch(diagnostic));
                }
            }

            if (result.IsEquivalent)
            {
                report.RoundTripEquivalentCount++;
                file.RoundTripEquivalentCount++;
                return;
            }

            report.RoundTripFailureCount++;
            file.RoundTripFailureCount++;
            if (report.RoundTripFailureLocations.Count >= 100)
                return;

            var details = string.Join(
                " | ",
                result.Diagnostics
                    .OrderBy(diagnostic => diagnostic.Severity is
                        Avm1CompilationDiagnosticSeverity.Error ? 0 : 1)
                    .ThenBy(diagnostic => diagnostic.Stage is
                        Avm1RecoveredMethodRoundTripStage.Comparison ? 0 : 1)
                    .Take(8)
                    .Select(diagnostic =>
                    $"{diagnostic.Stage} {diagnostic.Code}: {diagnostic.Message}"));
            report.RoundTripFailureLocations.Add(
                string.IsNullOrEmpty(details)
                    ? $"{path}: {status}"
                    : $"{path}: {status}: {details}");
            if (report.RoundTripFailureDetails.Count < 20)
            {
                report.RoundTripFailureDetails.Add(
                    $"{path}: {status}{Environment.NewLine}" +
                    "REFERENCE:" + Environment.NewLine +
                    DescribeRoundTripSource(
                        result.Compatibility?.ReferenceSource ?? result.Source) +
                    DescribeVerificationContext(result.ReferenceVerification) +
                    Environment.NewLine +
                    "CANDIDATE:" + Environment.NewLine +
                    DescribeRoundTripSource(result.Compatibility?.CandidateSource) +
                    DescribeFilteredCompilationPcode(result.Compilation, swfVersion));
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or
            StackOverflowException or
            OperationCanceledException))
        {
            Increment(report.RoundTripStatuses, "Exception");
            Increment(file.RoundTripStatuses, "Exception");
            Increment(
                report.RoundTripDiagnostics,
                $"Exception:{exception.GetType().Name}");
            report.RoundTripFailureCount++;
            file.RoundTripFailureCount++;
            if (report.RoundTripFailureLocations.Count < 100)
            {
                report.RoundTripFailureLocations.Add(
                    $"{path}: Exception: {exception.GetType().Name}: " +
                    exception.Message);
            }
        }
        finally
        {
            stopwatch.Stop();
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) -
                allocationStart;
            report.RoundTripMilliseconds += stopwatch.ElapsedMilliseconds;
            report.RoundTripAllocatedBytes += allocatedBytes;
            file.RoundTripMilliseconds += stopwatch.ElapsedMilliseconds;
            file.RoundTripAllocatedBytes += allocatedBytes;
        }
    }

    private static string DescribeRoundTripSource(Avm1SourceMethod? source)
    {
        if (source is null)
            return "<unavailable>";

        var symbols = string.Join(
            ", ",
            source.Arena.Symbols.Select(symbol =>
            {
                var name = symbol.Name.IsValid &&
                    symbol.Name.Value < source.Arena.Strings.Count
                    ? source.Arena[symbol.Name]
                    : "<unnamed>";
                return $"{symbol.Index.Value}:{symbol.Kind}/{name}/{symbol.Flags}";
            }));
        var text = source.GetAs2Text();
        const int maximumTextLength = 24000;
        if (text.Length > maximumTextLength)
            text = text[..maximumTextLength] + Environment.NewLine + "<truncated>";
        var normalization = Avm1SourceNormalizer.Normalize(
            source,
            CancellationToken.None);
        var normalizedSymbols = string.Join(
            ", ",
            normalization.Method.Arena.Symbols.Select(symbol =>
                $"{symbol.Index.Value}:{symbol.Kind}/" +
                normalization.Method.Arena[symbol.Name]));
        var normalizedText = normalization.Method.GetAs2Text();
        if (normalizedText.Length > maximumTextLength)
        {
            normalizedText = normalizedText[..maximumTextLength] +
                Environment.NewLine + "<truncated>";
        }
        return $"symbols=[{symbols}]{Environment.NewLine}{text}" +
            $"NORMALIZED rewrites={normalization.RewriteCount} " +
            $"symbols=[{normalizedSymbols}]{Environment.NewLine}" +
            normalizedText;
    }

    private static string DescribeFilteredCompilationPcode(
        Avm1MethodArtifact? compilation,
        byte swfVersion)
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(CodeUnitFilterEnvironmentVariable)) ||
            compilation?.ActionBody is null)
        {
            return string.Empty;
        }

        return Environment.NewLine + "COMPILED PCODE:" + Environment.NewLine +
            Avm1Disassembler.Disassemble(compilation.ActionBody.Actions, swfVersion) +
            Environment.NewLine;
    }

    private static string DescribeVerificationContext(
        ShockwaveFlash.Avm1.Compilation.Verification.Avm1BytecodeVerificationResult?
            verification)
    {
        if (verification is null || verification.Diagnostics.Count == 0)
            return string.Empty;

        var indices = verification.Diagnostics
            .Where(diagnostic => diagnostic.ActionIndex >= 0)
            .SelectMany(diagnostic => Enumerable.Range(
                Math.Max(0, diagnostic.ActionIndex - 16),
                Math.Min(
                    verification.Actions.Count - 1,
                    diagnostic.ActionIndex + 8) -
                Math.Max(0, diagnostic.ActionIndex - 16) + 1))
            .Distinct()
            .Order()
            .ToArray();
        if (indices.Length == 0)
            return string.Empty;

        return Environment.NewLine + "VERIFICATION ACTIONS:" +
            Environment.NewLine + string.Join(
                Environment.NewLine,
                indices.Select(index =>
                    $"#{index}@{verification.ActionOffsets[index]}: " +
                    DescribeAuditAction(verification.Actions[index])));
    }

    private static string DescribeAuditAction(Avm1Action action) => action switch
    {
        ActionPush push =>
            $"Push({string.Join(", ", push.PushValues.Select(DescribePushValue))})",
        ActionDefineFunction function =>
            $"DefineFunction name=\"{function.Name}\"",
        ActionDefineFunction2 function =>
            $"DefineFunction2 name=\"{function.Name}\"",
        ActionStoreRegister store => $"StoreRegister(r{store.RegisterNumber})",
        ActionIf conditional => $"If(offset={conditional.BranchOffset})",
        ActionJump jump => $"Jump(offset={jump.BranchOffset})",
        _ => action.Opcode.ToString()
    };

    private static string ClassifyRoundTripMismatch(
        Avm1SourceEquivalenceDiagnostic diagnostic)
    {
        if (diagnostic.Code != "AVM1EQ100")
            return diagnostic.Code;
        if (diagnostic.Path.StartsWith(".symbols", StringComparison.Ordinal))
            return "Symbols";
        if (diagnostic.Path.StartsWith(".functions", StringComparison.Ordinal))
            return "Functions";
        if (diagnostic.Path.StartsWith(".parameters", StringComparison.Ordinal))
            return "Parameters";
        if (diagnostic.Message.StartsWith("Statement count", StringComparison.Ordinal))
            return "StatementCount";
        if (diagnostic.Message.StartsWith("Statement kind", StringComparison.Ordinal))
            return "StatementKind";
        if (diagnostic.Message.StartsWith("Expression ", StringComparison.Ordinal))
            return "ExpressionKindOrOperator";
        if (diagnostic.Message.StartsWith("Expression child count", StringComparison.Ordinal))
            return "ExpressionChildCount";
        if (diagnostic.Message.StartsWith("Literal ", StringComparison.Ordinal))
            return "Literal";
        if (diagnostic.Path.Contains(".symbol", StringComparison.Ordinal))
            return "SymbolReference";
        return "Other";
    }

    private static string GetTemporaryReferenceContext(
        string text,
        IReadOnlyCollection<string> temporaryNames)
    {
        return string.Join(
            " | ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => temporaryNames.Any(name =>
                    Regex.IsMatch(
                        line,
                        $@"\b{Regex.Escape(name)}\b",
                        RegexOptions.CultureInvariant)))
                .Take(4));
    }

    private static string GetTemporarySurroundingContext(
        string text,
        IReadOnlyCollection<string> temporaryNames)
    {
        var lines = text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        var selected = new SortedSet<int>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!temporaryNames.Any(name => Regex.IsMatch(
                    lines[index],
                    $@"\b{Regex.Escape(name)}\b",
                    RegexOptions.CultureInvariant)))
            {
                continue;
            }

            for (var context = Math.Max(0, index - 2);
                context <= Math.Min(lines.Length - 1, index + 2);
                context++)
            {
                selected.Add(context);
            }
        }

        return string.Join(" | ", selected.Take(12).Select(index => lines[index].Trim()));
    }

    private static string GetTemporaryDefinitions(
        Avm1MethodDecompilation method,
        IReadOnlyCollection<string> temporaryNames)
    {
        return string.Join(
            ", ",
            temporaryNames.Select(name =>
            {
                var value = int.Parse(name.AsSpan(1));
                var definition = method.TacIr.Instructions.FirstOrDefault(instruction =>
                    instruction.Result.Value == value);
                return definition.Result.IsValid
                    ? $"{name}={definition.Op}@a{definition.Action.Value}"
                    : $"{name}=missing";
            }));
    }

    private static string NormalizeStackDiagnostic(string message)
    {
        if (message.StartsWith("Inconsistent stack depth at block", StringComparison.Ordinal))
            return "Inconsistent stack depth at CFG join.";
        if (message.StartsWith(
                "Inconsistent stack depth while constructing stack SSA",
                StringComparison.Ordinal))
        {
            return "Inconsistent stack depth while constructing stack SSA.";
        }
        if (message.StartsWith("Stack underflow", StringComparison.Ordinal))
            return message.Contains("peek", StringComparison.Ordinal)
                ? "Stack underflow on peek."
                : "Stack underflow.";
        return message;
    }

    private static void RecordStackErrorLocation(
        Avm1CorpusAuditReport report,
        string path,
        Avm1MethodDecompilation method,
        Avm1Diagnostic diagnostic)
    {
        if (!diagnostic.Action.IsValid ||
            diagnostic.Action.Value >= method.Instructions.Count ||
            report.StackErrorLocations.Count >= 100)
        {
            return;
        }

        var action = method.Instructions[diagnostic.Action];
        var first = Math.Max(0, diagnostic.Action.Value - 10);
        var last = Math.Min(method.Instructions.Count - 1, diagnostic.Action.Value + 10);
        var context = string.Join(", ", Enumerable.Range(first, last - first + 1)
            .Select(index =>
            {
                var instruction = method.Instructions[new ActionIndex(index)];
                var description =
                    $"#{index}@0x{instruction.Offset:X}:{DescribeAction(method.Instructions, instruction)}";
                return index == diagnostic.Action.Value
                    ? $"[{description}]"
                    : description;
            }));
        var blockText = method.ControlFlowGraph.TryGetBlockForAction(diagnostic.Action, out var blockIndex)
            ? $" Block {blockIndex.Value} " +
              $"[{method.ControlFlowGraph[blockIndex].StartAction.Value}.." +
              $"{method.ControlFlowGraph[blockIndex].EndAction.Value})."
            : string.Empty;
        report.StackErrorLocations.Add(
            $"{path}: action {diagnostic.Action.Value} at 0x{action.Offset:X}: " +
            $"{diagnostic.Message}{blockText} Context: {context}");
    }

    private static string DescribeAction(
        Avm1InstructionTable instructions,
        Avm1Instruction instruction)
    {
        return instruction.Action switch
        {
            ActionPush push => $"Push({string.Join(", ", push.PushValues.Select(DescribePushValue))})",
            ActionJump jump =>
                $"Jump({jump.BranchOffset:+#;-#;0}->#{GetBranchTarget(instructions, instruction.Index)})",
            ActionIf conditional =>
                $"If({conditional.BranchOffset:+#;-#;0}->#{GetBranchTarget(instructions, instruction.Index)})",
            ActionStoreRegister store => $"StoreRegister(r{store.RegisterNumber})",
            _ => instruction.Action.Opcode.ToString()
        };
    }

    private static int GetBranchTarget(
        Avm1InstructionTable instructions,
        ActionIndex action)
    {
        return instructions.GetBranchTargetAction(action).Value;
    }

    private static string DescribePushValue(PushValue value)
    {
        return value switch
        {
            PushValue.PushValueString text => $"\"{text.Value}\"",
            PushValue.PushValueBoolean boolean => boolean.Value ? "true" : "false",
            PushValue.PushValueInteger integer => integer.Value.ToString(),
            PushValue.PushValueFloat number => number.Value.ToString("R"),
            PushValue.PushValueDouble number => number.Value.ToString("R"),
            PushValue.PushValueRegister register => $"r{register.RegisterIndex}",
            PushValue.PushValueConstant8 constant => $"cp[{constant.ConstantIndex}]",
            PushValue.PushValueConstant16 constant => $"cp[{constant.ConstantIndex}]",
            PushValue.PushValueNull => "null",
            PushValue.PushValueUndefined => "undefined",
            _ => value.GetType().Name
        };
    }

    private static bool ContainsMalformedDescendant(
        IReadOnlyList<Avm1Action> actions,
        byte swfVersion,
        int depth = 0)
    {
        if (depth >= 128)
            return true;

        foreach (var action in actions)
        {
            switch (action)
            {
                case ActionDefineFunction function when ContainsMalformedBody(
                    function.Body,
                    swfVersion,
                    depth + 1):
                case ActionDefineFunction2 function2 when ContainsMalformedBody(
                    function2.Body,
                    swfVersion,
                    depth + 1):
                case ActionWith withAction when
                    ContainsMalformedBody(withAction.Body, swfVersion, depth + 1):
                    return true;
                case ActionTry tryAction when
                    ContainsMalformedBody(tryAction.TryBody, swfVersion, depth + 1) ||
                    ContainsMalformedBody(tryAction.CatchBody, swfVersion, depth + 1) ||
                    ContainsMalformedBody(tryAction.FinallyBody, swfVersion, depth + 1):
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsMalformedBody(
        ReadOnlyMemory<byte> body,
        byte swfVersion,
        int depth)
    {
        if (body.IsEmpty)
            return false;

        try
        {
            var actions = Avm1Action.DecodeCollection(
                body,
                swfVersion,
                Avm1ActionDecodeMode.RecoverMalformed);
            return actions.Any(action => action is ActionMalformed) ||
                ContainsMalformedDescendant(actions, swfVersion, depth);
        }
        catch
        {
            return true;
        }
    }

    private static void VisitNestedFunctions(
        string path,
        IReadOnlyList<Avm1Action> actions,
        byte swfVersion,
        IReadOnlyList<string>? inheritedConstantPool,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        IReadOnlyList<string>? activeConstantPool = inheritedConstantPool;
        var functionOrdinal = 0;
        for (var index = 0; index < actions.Count; index++)
        {
            switch (actions[index])
            {
                case ActionConstantPool constantPool:
                    activeConstantPool = constantPool.Constants;
                    break;

                case ActionDefineFunction2 function:
                    AuditNestedFunction(
                        $"{path}/function2[{functionOrdinal++}:{function.Name}]",
                        function.Body,
                        swfVersion,
                        new FunctionContext(function.Flags, function.Parameters),
                        function.RegisterCount,
                        activeConstantPool,
                        report,
                        file);
                    break;

                case ActionDefineFunction function:
                    AuditNestedFunction(
                        $"{path}/function[{functionOrdinal++}:{function.Name}]",
                        function.Body,
                        swfVersion,
                        new FunctionContext(
                            0,
                            function.Parameters.Select(parameter =>
                                new FunctionParameter(0, parameter)).ToArray()),
                        registerCount: null,
                        activeConstantPool,
                        report,
                        file);
                    break;

                case ActionWith withAction:
                    VisitStructuredBody(
                        path + $"/with[{index}]",
                        withAction.Body,
                        swfVersion,
                        activeConstantPool,
                        report,
                        file);
                    break;

                case ActionTry tryAction:
                    VisitStructuredBody(
                        path + $"/try[{index}]",
                        tryAction.TryBody,
                        swfVersion,
                        activeConstantPool,
                        report,
                        file);
                    VisitStructuredBody(
                        path + $"/catch[{index}]",
                        tryAction.CatchBody,
                        swfVersion,
                        activeConstantPool,
                        report,
                        file);
                    VisitStructuredBody(
                        path + $"/finally[{index}]",
                        tryAction.FinallyBody,
                        swfVersion,
                        activeConstantPool,
                        report,
                        file);
                    break;
            }
        }
    }

    private static void AuditNestedFunction(
        string path,
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        FunctionContext context,
        byte? registerCount,
        IReadOnlyList<string>? initialConstantPool,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        if (!TryDecodeActions(
                path,
                bytecode,
                swfVersion,
                report,
                file,
                out var actions,
                out var strictSucceeded))
        {
            return;
        }

        RecordCodecResult(
            bytecode,
            actions,
            swfVersion,
            strictSucceeded,
            report,
            file);

        AuditCodeUnit(
            path,
            bytecode,
            actions,
            swfVersion,
            context,
            registerCount,
            initialConstantPool,
            report,
            file);
        VisitNestedFunctions(
            path,
            actions,
            swfVersion,
            initialConstantPool,
            report,
            file);
    }

    private static void VisitStructuredBody(
        string path,
        ReadOnlyMemory<byte> body,
        byte swfVersion,
        IReadOnlyList<string>? inheritedConstantPool,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        if (body.IsEmpty)
            return;
        if (TryDecodeActions(
                path,
                body,
                swfVersion,
                report,
                file,
                out var actions,
                out _))
        {
            VisitNestedFunctions(
                path,
                actions,
                swfVersion,
                inheritedConstantPool,
                report,
                file);
        }
    }

    private static bool TryDecodeActions(
        string path,
        ReadOnlyMemory<byte> bytecode,
        byte swfVersion,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file,
        out IReadOnlyList<Avm1Action> actions,
        out bool strictSucceeded)
    {
        try
        {
            actions = Avm1Action.DecodeCollection(
                bytecode,
                swfVersion,
                Avm1ActionDecodeMode.RecoverMalformed);
        }
        catch (Exception exception)
        {
            actions = [];
            strictSucceeded = false;
            AddFailure(report, file, path, "recovery action decode", exception);
            return false;
        }

        var malformed = actions.OfType<ActionMalformed>().FirstOrDefault();
        var unknown = actions.OfType<ActionUnknown>().FirstOrDefault();
        if (malformed is not null || unknown is not null)
        {
            strictSucceeded = false;
            report.StrictDecodeFailureCount++;
            file.StrictDecodeFailureCount++;
            Increment(
                report.StrictDecodeFailures,
                malformed is not null
                    ? ClassifyMalformedAction(malformed)
                    : "UnknownOpcode");
            return true;
        }

        try
        {
            actions = Avm1Action.DecodeCollection(bytecode, swfVersion, strict: true);
            strictSucceeded = true;
            return true;
        }
        catch (Exception strictException)
        {
            strictSucceeded = false;
            report.StrictDecodeFailureCount++;
            file.StrictDecodeFailureCount++;
            Increment(
                report.StrictDecodeFailures,
                ClassifyStrictDecodeFailure(strictException));
            return true;
        }
    }

    private static string ClassifyMalformedAction(ActionMalformed action)
    {
        if (action.Reason.Contains("Invalid UTF-8", StringComparison.Ordinal))
            return "InvalidUtf8";
        if (action.Reason.Contains("declared", StringComparison.Ordinal) &&
            action.Reason.Contains("consumed", StringComparison.Ordinal))
        {
            return "TrailingPayload";
        }
        return "MalformedPayload";
    }

    private static void RecordCodecResult(
        ReadOnlyMemory<byte> bytecode,
        IReadOnlyList<Avm1Action> actions,
        byte swfVersion,
        bool strictSucceeded,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        var encoded = Avm1Action.EncodeCollection(actions, swfVersion);
        if (encoded.Span.SequenceEqual(bytecode.Span))
            return;

        report.CodecMismatchCount++;
        file.CodecMismatchCount++;
        if (!strictSucceeded)
        {
            report.RecoveryCodecMismatchCount++;
            file.RecoveryCodecMismatchCount++;
        }
    }

    private static string ClassifyStrictDecodeFailure(Exception exception)
    {
        if (exception.Message.Contains("invalid UTF-8", StringComparison.Ordinal))
            return "InvalidUtf8";
        if (exception.Message.Contains("Unknown AVM1 opcode", StringComparison.Ordinal))
            return "UnknownOpcode";
        if (exception.Message.Contains("after ActionEnd", StringComparison.Ordinal))
            return "TrailingAfterEnd";
        if (exception.Message.Contains("declared", StringComparison.Ordinal) &&
            exception.Message.Contains("consumed", StringComparison.Ordinal))
        {
            return "TrailingPayload";
        }
        return exception.GetType().Name;
    }

    private static void AuditClasses(
        ShockwaveFlashFile swf,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        if (file.MalformedActionCount > 0 ||
            file.ContainsMalformedCode ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                CodeUnitFilterEnvironmentVariable)) ||
            string.Equals(
                Environment.GetEnvironmentVariable(
                    SkipSourceProjectionEnvironmentVariable),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            report.ClassProjectionSkippedFileCount++;
            file.ClassProjectionSkipped = true;
            return;
        }

        try
        {
            var sourceFiles = Avm1ClassDecompiler.DecompileSourceFiles(swf);
            if (file.IsActionScript3 && sourceFiles.Count > 0)
            {
                throw new InvalidOperationException(
                    $"AVM1 class projection produced {sourceFiles.Count} files for an AS3 SWF.");
            }

            file.ClassFileCount = sourceFiles.Count;
            report.ClassFileCount += sourceFiles.Count;
            foreach (var sourceFile in sourceFiles)
            {
                if (!sourceFile.IsComplete)
                {
                    file.IncompleteClassFileCount++;
                    report.IncompleteClassFileCount++;
                }

                var text = sourceFile.GetAs2Text();
                var rawTemporaries = RawTemporaryPattern.Matches(text).Count;
                var temporaryReferences = TemporaryReferencePattern.Matches(text).Count;
                var versionedRegisters = VersionedRegisterPattern.Matches(text).Count;
                report.ClassRawTemporaryCount += rawTemporaries;
                file.ClassRawTemporaryCount += rawTemporaries;
                report.ClassTemporaryReferenceCount += temporaryReferences;
                file.ClassTemporaryReferenceCount += temporaryReferences;
                report.ClassVersionedRegisterCount += versionedRegisters;
                file.ClassVersionedRegisterCount += versionedRegisters;
                var classNames = string.Join(", ", sourceFile.Classes.Select(
                    sourceClass => sourceClass.Name.Value));
                if (rawTemporaries > 0 &&
                    report.ClassRawTemporaryLocations.Count < 100)
                {
                    report.ClassRawTemporaryLocations.Add(
                        $"{file.Path}: {classNames}");
                }
                if (temporaryReferences > 0 &&
                    report.ClassTemporaryReferenceLocations.Count < 100)
                {
                    report.ClassTemporaryReferenceLocations.Add(
                        $"{file.Path}: {classNames}");
                }
                if (versionedRegisters > 0 &&
                    report.ClassVersionedRegisterLocations.Count < 100)
                {
                    report.ClassVersionedRegisterLocations.Add(
                        $"{file.Path}: {classNames}");
                }
            }

            if (CompileRoundTripsEnabled())
            {
                AuditClassCompilation(
                    sourceFiles,
                    swf.Header.Version,
                    report,
                    file);
            }
        }
        catch (Exception exception)
        {
            AddFailure(report, file, file.Path, "class projection", exception);
        }
    }

    private static void AuditClassCompilation(
        IReadOnlyList<Avm1SourceFile> sourceFiles,
        byte swfVersion,
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file)
    {
        var sourceClasses = sourceFiles
            .SelectMany(sourceFile => sourceFile.Classes)
            .ToArray();
        report.ClassCompileAttemptCount += sourceClasses.Length;
        file.ClassCompileAttemptCount += sourceClasses.Length;
        if (sourceClasses.Length == 0)
            return;

        var allocationStart = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var program = Avm1SourceProgram.Create(sourceFiles);
            var compilation = new Avm1Compiler().CompileProgram(
                program,
                new Avm1ClassCompilationOptions(swfVersion));
            foreach (var diagnostic in compilation.Diagnostics)
                Increment(report.ClassCompileDiagnostics, diagnostic.Code);

            if (compilation.Classes.Count != sourceClasses.Length)
            {
                var missing = Math.Abs(sourceClasses.Length - compilation.Classes.Count);
                for (var i = 0; i < missing; i++)
                {
                    AddClassCompileFailure(
                        report,
                        file,
                        $"{file.Path}: compiler returned {compilation.Classes.Count} " +
                            $"artifacts for {sourceClasses.Length} source classes.");
                }
            }

            foreach (var artifact in compilation.Classes)
            {
                report.ClassCompileMethodCount += artifact.Methods.Count;
                file.ClassCompileMethodCount += artifact.Methods.Count;
                foreach (var diagnostic in artifact.Diagnostics)
                    Increment(report.ClassCompileDiagnostics, diagnostic.Code);
                if (!artifact.Succeeded)
                {
                    var errors = string.Join(
                        " | ",
                        artifact.Diagnostics
                            .Where(diagnostic => diagnostic.Severity is
                                Avm1CompilationDiagnosticSeverity.Error)
                            .Take(8)
                            .Select(diagnostic =>
                                $"{diagnostic.Code}: {diagnostic.Message}"));
                    AddClassCompileFailure(
                        report,
                        file,
                        $"{file.Path}: {artifact.Source.Name.Value}: compilation failed" +
                            (errors.Length == 0 ? string.Empty : $": {errors}"));
                    continue;
                }

                IReadOnlyList<Avm1Action> actions;
                try
                {
                    actions = Avm1Action.DecodeCollection(
                        artifact.Bytecode,
                        swfVersion,
                        strict: true);
                }
                catch (Exception exception) when (exception is not (
                    OutOfMemoryException or
                    StackOverflowException or
                    OperationCanceledException))
                {
                    report.ClassCompileStrictDecodeFailureCount++;
                    file.ClassCompileStrictDecodeFailureCount++;
                    AddClassCompileFailure(
                        report,
                        file,
                        $"{file.Path}: {artifact.Source.Name.Value}: strict decode " +
                            $"failed: {exception.GetType().Name}: {exception.Message}");
                    continue;
                }

                var compiledInitializer = Avm1Decompiler.DecompileMethod(
                    actions,
                    swfVersion);
                var metadata = Avm1ClassMetadataAnalysis.Build(
                    compiledInitializer.Core,
                    artifact.Source.Name.Value);
                var expectedBase = Avm1ClassMetadataAnalysis.NormalizeQualifiedName(
                    artifact.Source.BaseType?.Value);
                var actualBase = Avm1ClassMetadataAnalysis.NormalizeQualifiedName(
                    metadata.BaseClassName);
                var expectedInterfaces = artifact.Source.Interfaces
                    .Select(item =>
                        Avm1ClassMetadataAnalysis.NormalizeQualifiedName(item.Value))
                    .ToArray();
                var actualInterfaces = metadata.InterfaceNames
                    .Select(Avm1ClassMetadataAnalysis.NormalizeQualifiedName)
                    .ToArray();
                if (!string.Equals(expectedBase, actualBase, StringComparison.Ordinal) ||
                    !expectedInterfaces.SequenceEqual(
                        actualInterfaces,
                        StringComparer.Ordinal))
                {
                    report.ClassCompileMetadataMismatchCount++;
                    file.ClassCompileMetadataMismatchCount++;
                    AddClassCompileFailure(
                        report,
                        file,
                        $"{file.Path}: {artifact.Source.Name.Value}: class metadata " +
                            $"mismatch; base expected '{expectedBase}', actual " +
                            $"'{actualBase}'; interfaces expected " +
                            $"[{string.Join(", ", expectedInterfaces)}], actual " +
                            $"[{string.Join(", ", actualInterfaces)}].");
                    continue;
                }

                report.ClassCompileSuccessCount++;
                file.ClassCompileSuccessCount++;
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or
            StackOverflowException or
            OperationCanceledException))
        {
            var remaining = Math.Max(
                1,
                file.ClassCompileAttemptCount - file.ClassCompileSuccessCount -
                    file.ClassCompileFailureCount);
            for (var i = 0; i < remaining; i++)
            {
                AddClassCompileFailure(
                    report,
                    file,
                    $"{file.Path}: class compilation audit failed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
            }
        }
        finally
        {
            stopwatch.Stop();
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) -
                allocationStart;
            report.ClassCompileMilliseconds += stopwatch.ElapsedMilliseconds;
            report.ClassCompileAllocatedBytes += allocatedBytes;
            file.ClassCompileMilliseconds += stopwatch.ElapsedMilliseconds;
            file.ClassCompileAllocatedBytes += allocatedBytes;
        }
    }

    private static bool CompileRoundTripsEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                CompileRoundTripEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static void AddClassCompileFailure(
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file,
        string message)
    {
        report.ClassCompileFailureCount++;
        file.ClassCompileFailureCount++;
        report.Failures.Add(message);
        file.Failures.Add(message);
        if (report.ClassCompileFailureLocations.Count < 100)
            report.ClassCompileFailureLocations.Add(message);
    }

    private static void CollectActionBlobs(
        IEnumerable<Tag> tags,
        string path,
        byte swfVersion,
        List<Avm1ActionBlob> result)
    {
        var tagIndex = 0;
        foreach (var tag in tags)
        {
            var tagPath = $"{path}/{tag.GetType().Name}[{tagIndex++}]";
            switch (tag)
            {
                case DoActionTag doAction:
                    result.Add(new Avm1ActionBlob(tagPath, doAction.Data, swfVersion));
                    break;

                case DoInitActionTag doInit:
                    result.Add(new Avm1ActionBlob(tagPath, doInit.Data, swfVersion));
                    break;

                case DefineButtonTag button:
                    AddButtonActions(button.Actions, tagPath, swfVersion, result);
                    break;

                case DefineButton2Tag button:
                    AddButtonActions(button.Actions, tagPath, swfVersion, result);
                    break;

                case PlaceObject2Tag placeObject:
                    AddClipActions(
                        placeObject.ClipActions,
                        tagPath,
                        swfVersion,
                        result);
                    break;

                case PlaceObject3Tag placeObject:
                    AddClipActions(
                        placeObject.ClipActions,
                        tagPath,
                        swfVersion,
                        result);
                    break;

                case PlaceObject4Tag placeObject:
                    AddClipActions(
                        placeObject.Placement.ClipActions,
                        tagPath,
                        swfVersion,
                        result);
                    break;

                case DefineSpriteTag sprite:
                    CollectActionBlobs(
                        sprite.Tags,
                        tagPath,
                        swfVersion,
                        result);
                    break;
            }
        }
    }

    private static void AddButtonActions(
        IReadOnlyList<ShockwaveFlash.Types.Button.ButtonAction> actions,
        string path,
        byte swfVersion,
        List<Avm1ActionBlob> result)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            result.Add(new Avm1ActionBlob(
                $"{path}/buttonAction[{index}]",
                actions[index].Data,
                swfVersion));
        }
    }

    private static void AddClipActions(
        IReadOnlyList<ClipAction>? actions,
        string path,
        byte swfVersion,
        List<Avm1ActionBlob> result)
    {
        if (actions is null)
            return;
        for (var index = 0; index < actions.Count; index++)
        {
            result.Add(new Avm1ActionBlob(
                $"{path}/clipAction[{index}]",
                actions[index].Data,
                swfVersion));
        }
    }

    private static void AddFailure(
        Avm1CorpusAuditReport report,
        Avm1CorpusFileAudit file,
        string path,
        string stage,
        Exception exception)
    {
        var message = $"{path}: {stage}: {exception.GetType().Name}: {exception.Message}";
        report.Failures.Add(message);
        if (report.FailureDetails.Count < 100)
            report.FailureDetails.Add($"{path}: {stage}{Environment.NewLine}{exception}");
        file.Failures.Add(message);
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private sealed record Avm1ActionBlob(
        string Path,
        ReadOnlyMemory<byte> Bytecode,
        byte SwfVersion);
}

internal sealed class Avm1CorpusAuditReport
{
    public List<Avm1CorpusFileAudit> Files { get; } = [];

    public List<string> Failures { get; } = [];

    public List<string> FailureDetails { get; } = [];

    public Dictionary<string, int> UnknownOpcodes { get; } = [];

    public Dictionary<string, int> StrictDecodeFailures { get; } = [];

    public Dictionary<string, int> StackDiagnostics { get; } = [];

    public Dictionary<string, int> SourceErrors { get; } = [];

    public Dictionary<string, int> SourceWarnings { get; } = [];

    public Dictionary<string, int> SourceInformation { get; } = [];

    public Dictionary<string, int> RoundTripStatuses { get; } = [];

    public Dictionary<string, int> RoundTripDiagnostics { get; } = [];

    public Dictionary<string, int> RoundTripMismatches { get; } = [];

    public Dictionary<string, int> ClassCompileDiagnostics { get; } = [];

    public Dictionary<string, long> CoreStageMilliseconds { get; } = [];

    public Dictionary<string, long> CoreStageAllocatedBytes { get; } = [];

    public List<string> RawTemporaryLocations { get; } = [];

    public List<string> RawTemporaryDetails { get; } = [];

    public List<string> TemporaryReferenceLocations { get; } = [];

    public List<string> VersionedRegisterLocations { get; } = [];

    public List<string> UnboundTemporaryLocations { get; } = [];

    public List<string> SelfInitializingTemporaryLocations { get; } = [];

    public List<string> EmptyIfStatementLocations { get; } = [];

    public List<string> ClassRawTemporaryLocations { get; } = [];

    public List<string> ClassTemporaryReferenceLocations { get; } = [];

    public List<string> ClassVersionedRegisterLocations { get; } = [];

    public List<string> SourceErrorLocations { get; } = [];

    public List<string> StackErrorLocations { get; } = [];

    public List<string> RoundTripFailureLocations { get; } = [];

    public List<string> RoundTripFailureDetails { get; } = [];

    public List<string> ClassCompileFailureLocations { get; } = [];

    public int ActionBlobCount { get; set; }

    public int CodeUnitCount { get; set; }

    public int ActionCount { get; set; }

    public int CodecMismatchCount { get; set; }

    public int StrictDecodeFailureCount { get; set; }

    public int RecoveryCodecMismatchCount { get; set; }

    public int MalformedActionCount { get; set; }

    public int SkippedMalformedProjectionCount { get; set; }

    public int ClassProjectionSkippedFileCount { get; set; }

    public int MalformedFileCount { get; set; }

    public int SkippedSourceProjectionCount { get; set; }

    public int MaximumCodeUnitActionCount { get; set; }

    public string MaximumCodeUnitPath { get; set; } = string.Empty;

    public int SourceProjectionCount { get; set; }

    public long SourceProjectionMilliseconds { get; set; }

    public long SourceProjectionAllocatedBytes { get; set; }

    public int RoundTripAttemptCount { get; set; }

    public int RoundTripEquivalentCount { get; set; }

    public int RoundTripFailureCount { get; set; }

    public long RoundTripMilliseconds { get; set; }

    public long RoundTripAllocatedBytes { get; set; }

    public long MaximumSourceProjectionMilliseconds { get; set; }

    public long MaximumSourceProjectionAllocatedBytes { get; set; }

    public string MaximumSourceProjectionPath { get; set; } = string.Empty;

    public int CoreAuditCount { get; set; }

    public long CoreAuditMilliseconds { get; set; }

    public long CoreAuditAllocatedBytes { get; set; }

    public long CoreAnalysisMilliseconds { get; set; }

    public long CoreAnalysisAllocatedBytes { get; set; }

    public long ValueAnalysisMilliseconds { get; set; }

    public long ValueAnalysisAllocatedBytes { get; set; }

    public long SwitchAnalysisMilliseconds { get; set; }

    public long SwitchAnalysisAllocatedBytes { get; set; }

    public long ScopeAnalysisMilliseconds { get; set; }

    public long ScopeAnalysisAllocatedBytes { get; set; }

    public long AstRecoveryMilliseconds { get; set; }

    public long AstRecoveryAllocatedBytes { get; set; }

    public long SourceModelProjectionMilliseconds { get; set; }

    public long SourceModelProjectionAllocatedBytes { get; set; }

    public int SourceExpressionCount { get; set; }

    public int SourceStatementCount { get; set; }

    public int SourceSymbolCount { get; set; }

    public int SourceOriginCount { get; set; }

    public int MaximumSourceExpressionCount { get; set; }

    public int MaximumSourceStatementCount { get; set; }

    public int MaximumSourceSymbolCount { get; set; }

    public int MaximumSourceOriginCount { get; set; }

    public string MaximumSourceArenaPath { get; set; } = string.Empty;

    public long SourceEmissionMilliseconds { get; set; }

    public long SourceEmissionAllocatedBytes { get; set; }

    public int MaximumCodeUnitBlockCount { get; set; }

    public string MaximumCodeUnitBlockPath { get; set; } = string.Empty;

    public int UnknownActionCount { get; set; }

    public int StackDiagnosticCount { get; set; }

    public int SourceErrorCount { get; set; }

    public int SourceWarningCount { get; set; }

    public int OpaqueRegionCount { get; set; }

    public int IrreducibleCodeUnitCount { get; set; }

    public int RawTemporaryCount { get; set; }

    public int TemporaryReferenceCount { get; set; }

    public int VersionedRegisterCount { get; set; }

    public int UnboundTemporaryCount { get; set; }

    public int SelfInitializingTemporaryCount { get; set; }

    public int EmptyIfStatementCount { get; set; }

    public int ClassFileCount { get; set; }

    public int IncompleteClassFileCount { get; set; }

    public int ClassRawTemporaryCount { get; set; }

    public int ClassTemporaryReferenceCount { get; set; }

    public int ClassVersionedRegisterCount { get; set; }

    public int ClassCompileAttemptCount { get; set; }

    public int ClassCompileSuccessCount { get; set; }

    public int ClassCompileFailureCount { get; set; }

    public int ClassCompileStrictDecodeFailureCount { get; set; }

    public int ClassCompileMetadataMismatchCount { get; set; }

    public int ClassCompileMethodCount { get; set; }

    public long ClassCompileMilliseconds { get; set; }

    public long ClassCompileAllocatedBytes { get; set; }

    public string FormatSummary() =>
        $"files={Files.Count}, blobs={ActionBlobCount}, codeUnits={CodeUnitCount}, " +
        $"actions={ActionCount}, failures={Failures.Count}, " +
        $"strictRejects={StrictDecodeFailureCount}, " +
        $"codecMismatches={CodecMismatchCount}, malformed={MalformedActionCount}, " +
        $"unknown={UnknownActionCount}, " +
        $"sourceErrors={SourceErrorCount}, opaque={OpaqueRegionCount}" +
        (RoundTripAttemptCount > 0
            ? $", roundTrips={RoundTripEquivalentCount}/{RoundTripAttemptCount}"
            : string.Empty) +
        (ClassCompileAttemptCount > 0
            ? $", classCompiles={ClassCompileSuccessCount}/{ClassCompileAttemptCount}"
            : string.Empty);
}

internal sealed class Avm1CorpusFileAudit
{
    public string Path { get; set; } = string.Empty;

    public long ByteLength { get; set; }

    public byte SwfVersion { get; set; }

    public bool IsActionScript3 { get; set; }

    public long ElapsedMilliseconds { get; set; }

    public int ActionBlobCount { get; set; }

    public bool ContainsMalformedCode { get; set; }

    public int CodeUnitCount { get; set; }

    public int ActionCount { get; set; }

    public int CodecMismatchCount { get; set; }

    public int StrictDecodeFailureCount { get; set; }

    public int RecoveryCodecMismatchCount { get; set; }

    public int MalformedActionCount { get; set; }

    public int SkippedMalformedProjectionCount { get; set; }

    public bool ClassProjectionSkipped { get; set; }

    public int SkippedSourceProjectionCount { get; set; }

    public int MaximumCodeUnitActionCount { get; set; }

    public string MaximumCodeUnitPath { get; set; } = string.Empty;

    public int SourceProjectionCount { get; set; }

    public long SourceProjectionMilliseconds { get; set; }

    public long SourceProjectionAllocatedBytes { get; set; }

    public int RoundTripAttemptCount { get; set; }

    public int RoundTripEquivalentCount { get; set; }

    public int RoundTripFailureCount { get; set; }

    public long RoundTripMilliseconds { get; set; }

    public long RoundTripAllocatedBytes { get; set; }

    public Dictionary<string, int> RoundTripStatuses { get; } = [];

    public int UnknownActionCount { get; set; }

    public int StackDiagnosticCount { get; set; }

    public int SourceErrorCount { get; set; }

    public int SourceWarningCount { get; set; }

    public int OpaqueRegionCount { get; set; }

    public int IrreducibleCodeUnitCount { get; set; }

    public int RawTemporaryCount { get; set; }

    public int TemporaryReferenceCount { get; set; }

    public int VersionedRegisterCount { get; set; }

    public int UnboundTemporaryCount { get; set; }

    public int SelfInitializingTemporaryCount { get; set; }

    public int EmptyIfStatementCount { get; set; }

    public int ClassFileCount { get; set; }

    public int IncompleteClassFileCount { get; set; }

    public int ClassRawTemporaryCount { get; set; }

    public int ClassTemporaryReferenceCount { get; set; }

    public int ClassVersionedRegisterCount { get; set; }

    public int ClassCompileAttemptCount { get; set; }

    public int ClassCompileSuccessCount { get; set; }

    public int ClassCompileFailureCount { get; set; }

    public int ClassCompileStrictDecodeFailureCount { get; set; }

    public int ClassCompileMetadataMismatchCount { get; set; }

    public int ClassCompileMethodCount { get; set; }

    public long ClassCompileMilliseconds { get; set; }

    public long ClassCompileAllocatedBytes { get; set; }

    public List<string> Failures { get; } = [];
}
