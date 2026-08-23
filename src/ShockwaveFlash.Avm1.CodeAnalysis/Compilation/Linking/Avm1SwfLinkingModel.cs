using ShockwaveFlash.Tags;

namespace ShockwaveFlash.Avm1.Compilation;

public enum Avm1SwfPlacementKind : byte
{
    DoInitAction,
    DoAction
}

public sealed record Avm1SwfPlacement
{
    public Avm1SwfPlacement(
        Avm1ClassArtifact artifact,
        Avm1SwfPlacementKind kind)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Artifact = artifact;
        Kind = kind;
    }

    public Avm1ClassArtifact Artifact { get; }

    public Avm1SwfPlacementKind Kind { get; }

    public ushort SpriteId { get; init; }

    public int FrameIndex { get; init; }

    public int? ReplaceTagIndex { get; init; }

    public string? ExportName { get; init; }
}

public sealed class Avm1SwfPlacementPlan
{
    private readonly Avm1SwfPlacement[] _placements;

    public Avm1SwfPlacementPlan(IEnumerable<Avm1SwfPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        _placements = placements.ToArray();
        if (_placements.Any(static placement => placement is null))
        {
            throw new ArgumentException(
                "A placement plan cannot contain null entries.",
                nameof(placements));
        }
    }

    public IReadOnlyList<Avm1SwfPlacement> Placements => _placements;

    public static Avm1SwfPlacementPlan FromProgram(Avm1ProgramArtifact program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new Avm1SwfPlacementPlan(program.Classes
            .Where(sourceClass =>
                sourceClass.Source.Origin.SpriteId != 0 &&
                (sourceClass.AbiProfile is not
                    Avm1ClassAbiProfile.OriginPreserving ||
                 sourceClass.Source.Origin.HasInitializer))
            .Select(sourceClass => new Avm1SwfPlacement(
                sourceClass,
                Avm1SwfPlacementKind.DoInitAction)
            {
                SpriteId = sourceClass.Source.Origin.SpriteId,
                ExportName = sourceClass.Source.Origin.RuntimeName
            }));
    }
}

public sealed record Avm1SwfLinkOptions
{
    public bool DryRun { get; init; }

    public bool EnsureExportAsset { get; init; } = true;

    public Avm1SwfDebuggerOptions? Debugger { get; init; }
}

public sealed record Avm1SwfDebuggerOptions(Guid DebugId)
{
    public string PasswordHash { get; init; } = string.Empty;
}

public readonly record struct Avm1SwfDebuggerInfo(
    Guid DebugId,
    string PasswordHash);

public enum Avm1SwfChangeKind : byte
{
    InsertTag,
    ReplaceTag,
    UpdateExportAssets
}

public readonly record struct Avm1SwfChange(
    Avm1SwfChangeKind Kind,
    TagCode TagCode,
    int TagIndex,
    ushort SpriteId,
    string Description);

public readonly record struct Avm1SwfTagByteRange(
    int TagIndex,
    int TagStreamOffset,
    int Length,
    string Sha256);

public readonly record struct Avm1SwfPatchRange(
    Avm1SwfChangeKind Kind,
    TagCode TagCode,
    ushort SpriteId,
    Avm1SwfTagByteRange? Original,
    Avm1SwfTagByteRange Result,
    bool ByteIdentical,
    string Description);

public sealed class Avm1SwfPatchReport
{
    private readonly Avm1SwfPatchRange[] _ranges;

    internal Avm1SwfPatchReport(
        ShockwaveFlashCompression compression,
        int originalFileLength,
        int resultFileLength,
        string originalFileSha256,
        string resultFileSha256,
        Avm1SwfPatchRange[] ranges)
    {
        Compression = compression;
        OriginalFileLength = originalFileLength;
        ResultFileLength = resultFileLength;
        OriginalFileSha256 = originalFileSha256;
        ResultFileSha256 = resultFileSha256;
        _ranges = ranges;
    }

    public ShockwaveFlashCompression Compression { get; }

    public int OriginalFileLength { get; }

    public int ResultFileLength { get; }

    public string OriginalFileSha256 { get; }

    public string ResultFileSha256 { get; }

    public IReadOnlyList<Avm1SwfPatchRange> Ranges => _ranges;

    public bool IsByteIdentical =>
        OriginalFileLength == ResultFileLength &&
        OriginalFileSha256 == ResultFileSha256;
}

public readonly record struct Avm1SwfLinkDiagnostic(
    string Code,
    Avm1CompilationDiagnosticSeverity Severity,
    int TagIndex,
    string Message);

public sealed class Avm1SwfLinkResult
{
    private readonly Avm1SwfChange[] _changes;
    private readonly Avm1SwfLinkDiagnostic[] _diagnostics;

    internal Avm1SwfLinkResult(
        ShockwaveFlashFile file,
        bool dryRun,
        bool applied,
        Avm1SwfChange[] changes,
        Avm1SwfLinkDiagnostic[] diagnostics,
        Avm1SwfPatchReport? patchReport,
        Avm1LinkedSourceMap sourceMap,
        Avm1SwfDebuggerInfo? debugger)
    {
        File = file;
        DryRun = dryRun;
        Applied = applied;
        _changes = changes;
        _diagnostics = diagnostics;
        PatchReport = patchReport;
        SourceMap = sourceMap;
        Debugger = debugger;
    }

    public ShockwaveFlashFile File { get; }

    public bool DryRun { get; }

    public bool Applied { get; }

    public IReadOnlyList<Avm1SwfChange> Changes => _changes;

    public IReadOnlyList<Avm1SwfLinkDiagnostic> Diagnostics => _diagnostics;

    public Avm1SwfPatchReport? PatchReport { get; }

    public Avm1LinkedSourceMap SourceMap { get; }

    public Avm1SwfDebuggerInfo? Debugger { get; }

    public bool Succeeded => _diagnostics.All(diagnostic =>
        diagnostic.Severity is not Avm1CompilationDiagnosticSeverity.Error);
}
