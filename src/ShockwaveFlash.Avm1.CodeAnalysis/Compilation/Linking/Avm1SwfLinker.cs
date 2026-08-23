using System.Security.Cryptography;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Action;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Tags.DisplayList;
using ShockwaveFlash.Tags.Metadata;
using ShockwaveFlash.Tags.Sprite;
using ShockwaveFlash.Types.Control;

namespace ShockwaveFlash.Avm1.Compilation;

public sealed class Avm1SwfLinker
{
    public static Avm1SwfLinkResult Link(
        ShockwaveFlashFile file,
        Avm1SwfPlacementPlan plan,
        Avm1SwfLinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new Avm1SwfLinkOptions();

        var diagnostics = new List<Avm1SwfLinkDiagnostic>();
        var pendingChanges = new List<PendingChange>();
        var pendingSourceMaps = new List<PendingSourceMap>();
        var originalTags = file.Tags.ToArray();
        var tags = file.Tags.ToList();
        if (file.Tags.OfType<FileAttributesTag>().Any(tag => tag.IsActionScript3))
        {
            AddError(
                diagnostics,
                "AVM1LNK001",
                -1,
                "AVM1 code cannot be linked into a SWF whose ActionScript3 flag is set.");
        }

        var doInitIds = new HashSet<ushort>();
        var replacementIndices = new HashSet<int>();
        for (var placementIndex = 0;
             placementIndex < plan.Placements.Count;
             placementIndex++)
        {
            var placement = plan.Placements[placementIndex];
            if (HasErrors(diagnostics))
                break;
            ValidatePlacement(
                file,
                tags,
                placement,
                doInitIds,
                replacementIndices,
                diagnostics);
            if (HasErrors(diagnostics))
                break;

            Tag? linkedTag;
            switch (placement.Kind)
            {
                case Avm1SwfPlacementKind.DoInitAction:
                    linkedTag = LinkDoInitAction(
                        tags,
                        placement,
                        options,
                        pendingChanges,
                        diagnostics);
                    break;
                case Avm1SwfPlacementKind.DoAction:
                    linkedTag = LinkDoAction(
                        tags,
                        placement,
                        pendingChanges,
                        diagnostics);
                    break;
                default:
                    linkedTag = null;
                    AddError(
                        diagnostics,
                        "AVM1LNK002",
                        -1,
                        $"Placement kind {placement.Kind} is not supported.");
                    break;
            }
            if (linkedTag is not null)
            {
                pendingSourceMaps.Add(new PendingSourceMap(
                    placementIndex,
                    placement,
                    linkedTag));
            }
        }

        if (!HasErrors(diagnostics) && options.Debugger is { } debuggerOptions)
        {
            EnsureDebuggerTags(
                file.Header.Version,
                tags,
                debuggerOptions,
                pendingChanges,
                diagnostics);
        }

        var succeeded = !HasErrors(diagnostics);
        var changes = CreateChanges(tags, pendingChanges);
        var patchReport = succeeded
            ? CreatePatchReport(file.Header, originalTags, tags, pendingChanges)
            : null;
        var sourceMap = succeeded
            ? CreateSourceMap(
                file.Header,
                tags,
                pendingChanges,
                pendingSourceMaps)
            : Avm1LinkedSourceMap.Empty;
        var applied = succeeded && !options.DryRun;
        if (applied)
            file.Tags = tags;
        var debugger = succeeded && options.Debugger is { } requestedDebugger
            ? new Avm1SwfDebuggerInfo(
                requestedDebugger.DebugId,
                requestedDebugger.PasswordHash)
            : (Avm1SwfDebuggerInfo?)null;
        return new Avm1SwfLinkResult(
            file,
            options.DryRun,
            applied,
            changes,
            diagnostics.ToArray(),
            patchReport,
            sourceMap,
            debugger);
    }

    private static void EnsureDebuggerTags(
        byte swfVersion,
        List<Tag> tags,
        Avm1SwfDebuggerOptions options,
        List<PendingChange> changes,
        List<Avm1SwfLinkDiagnostic> diagnostics)
    {
        if (swfVersion < 6)
        {
            AddError(
                diagnostics,
                "AVM1LNK015",
                -1,
                "SWD debugging requires SWF version 6 or later.");
            return;
        }
        if (options.DebugId == Guid.Empty)
        {
            AddError(
                diagnostics,
                "AVM1LNK016",
                -1,
                "SWD debugging requires a non-empty DebugID.");
            return;
        }
        if (options.PasswordHash is null ||
            options.PasswordHash.Contains('\0', StringComparison.Ordinal))
        {
            AddError(
                diagnostics,
                "AVM1LNK017",
                -1,
                "The debugger password hash cannot be null or contain a null character.");
            return;
        }

        var enableIndices = tags
            .Select((tag, index) => (tag, index))
            .Where(item => item.tag is EnableDebuggerTag or EnableDebugger2Tag)
            .Select(item => item.index)
            .ToArray();
        var idIndices = tags
            .Select((tag, index) => (tag, index))
            .Where(item => item.tag is DebugIdTag)
            .Select(item => item.index)
            .ToArray();
        if (enableIndices.Length > 1 || idIndices.Length > 1)
        {
            AddError(
                diagnostics,
                "AVM1LNK018",
                enableIndices.Skip(1).Concat(idIndices.Skip(1)).First(),
                "The destination SWF contains duplicate debugger metadata tags.");
            return;
        }

        EnableDebugger2Tag enableTag;
        if (enableIndices.Length == 1)
        {
            var index = enableIndices[0];
            var original = tags[index];
            if (original is EnableDebugger2Tag current &&
                current.Password == options.PasswordHash)
            {
                enableTag = current;
            }
            else
            {
                enableTag = new EnableDebugger2Tag(
                    new TagMetadata(
                        TagCode.EnableDebugger2,
                        original.Metadata.Offset,
                        original.Metadata.Length),
                    options.PasswordHash);
                tags[index] = enableTag;
                changes.Add(new PendingChange(
                    Avm1SwfChangeKind.ReplaceTag,
                    TagCode.EnableDebugger2,
                    SpriteId: 0,
                    original,
                    enableTag,
                    "Replace the SWF debugger-enablement tag."));
            }
        }
        else
        {
            var insertIndex = FindDebuggerTagInsertionIndex(tags);
            enableTag = new EnableDebugger2Tag(
                new TagMetadata(TagCode.EnableDebugger2, 0, 0),
                options.PasswordHash);
            tags.Insert(insertIndex, enableTag);
            changes.Add(new PendingChange(
                Avm1SwfChangeKind.InsertTag,
                TagCode.EnableDebugger2,
                SpriteId: 0,
                OriginalTag: null,
                enableTag,
                "Insert the SWF debugger-enablement tag."));
        }

        if (idIndices.Length == 1)
        {
            var original = tags.OfType<DebugIdTag>().Single();
            if (original.Id == options.DebugId)
                return;
            var replacement = new DebugIdTag(
                original.Metadata,
                options.DebugId);
            var index = tags.IndexOf(original);
            tags[index] = replacement;
            changes.Add(new PendingChange(
                Avm1SwfChangeKind.ReplaceTag,
                TagCode.DebugId,
                SpriteId: 0,
                original,
                replacement,
                "Replace the SWF DebugID tag."));
            return;
        }

        var debugId = new DebugIdTag(
            new TagMetadata(TagCode.DebugId, 0, 0),
            options.DebugId);
        tags.Insert(tags.IndexOf(enableTag) + 1, debugId);
        changes.Add(new PendingChange(
            Avm1SwfChangeKind.InsertTag,
            TagCode.DebugId,
            SpriteId: 0,
            OriginalTag: null,
            debugId,
            "Insert the SWF DebugID tag."));
    }

    private static int FindDebuggerTagInsertionIndex(List<Tag> tags)
    {
        var index = 0;
        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i] is FileAttributesTag or MetadataTag)
                index = i + 1;
        }
        return index;
    }

    private static void ValidatePlacement(
        ShockwaveFlashFile file,
        List<Tag> tags,
        Avm1SwfPlacement placement,
        HashSet<ushort> doInitIds,
        HashSet<int> replacementIndices,
        List<Avm1SwfLinkDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(placement.Kind))
        {
            AddError(
                diagnostics,
                "AVM1LNK002",
                -1,
                $"Placement kind {placement.Kind} is not valid.");
            return;
        }
        if (!placement.Artifact.Succeeded)
        {
            AddError(
                diagnostics,
                "AVM1LNK003",
                -1,
                $"Class {placement.Artifact.Source.Name} did not compile successfully.");
        }
        if (placement.Artifact.SwfVersion > file.Header.Version)
        {
            AddError(
                diagnostics,
                "AVM1LNK004",
                -1,
                $"Class {placement.Artifact.Source.Name} targets SWF " +
                $"{placement.Artifact.SwfVersion}, but the destination is SWF " +
                $"{file.Header.Version}.");
        }
        if (placement.ReplaceTagIndex is { } replaceIndex &&
            (!replacementIndices.Add(replaceIndex) ||
             replaceIndex < 0 ||
             replaceIndex >= tags.Count))
        {
            AddError(
                diagnostics,
                "AVM1LNK005",
                replaceIndex,
                $"Replacement tag index {replaceIndex} is invalid or used more than once.");
        }

        if (placement.Kind is not Avm1SwfPlacementKind.DoInitAction)
            return;
        if (placement.ReplaceTagIndex.HasValue)
        {
            AddError(
                diagnostics,
                "AVM1LNK014",
                placement.ReplaceTagIndex.Value,
                "DoInitAction placement is resolved by sprite ID and cannot use ReplaceTagIndex.");
            return;
        }
        var spriteId = GetSpriteId(placement);
        if (spriteId == 0)
        {
            AddError(
                diagnostics,
                "AVM1LNK006",
                -1,
                $"Class {placement.Artifact.Source.Name} has no sprite ID for DoInitAction placement.");
        }
        else if (!doInitIds.Add(spriteId))
        {
            AddError(
                diagnostics,
                "AVM1LNK007",
                -1,
                $"Sprite {spriteId} occurs more than once in the placement plan.");
        }
    }

    private static DoInitActionTag? LinkDoInitAction(
        List<Tag> tags,
        Avm1SwfPlacement placement,
        Avm1SwfLinkOptions options,
        List<PendingChange> changes,
        List<Avm1SwfLinkDiagnostic> diagnostics)
    {
        var spriteId = GetSpriteId(placement);
        if (options.EnsureExportAsset)
        {
            EnsureExportAsset(
                tags,
                spriteId,
                placement.ExportName ??
                    placement.Artifact.Source.Origin.RuntimeName ??
                    placement.Artifact.Source.Name.Value,
                changes,
                diagnostics);
            if (HasErrors(diagnostics))
                return null;
        }

        var spriteIndex = tags.FindIndex(tag =>
            tag is DefineSpriteTag sprite && sprite.Id == spriteId);
        if (spriteIndex < 0)
        {
            AddError(
                diagnostics,
                "AVM1LNK008",
                -1,
                $"Destination SWF has no DefineSprite tag for sprite {spriteId}.");
            return null;
        }

        var existing = tags
            .Select((tag, index) => (tag, index))
            .Where(item => item.tag is DoInitActionTag init && init.Id == spriteId)
            .Select(item => item.index)
            .ToArray();
        if (existing.Length > 1)
        {
            AddError(
                diagnostics,
                "AVM1LNK009",
                existing[1],
                $"Destination SWF contains multiple DoInitAction tags for sprite {spriteId}.");
            return null;
        }

        if (existing.Length == 1)
        {
            var tagIndex = existing[0];
            var original = (DoInitActionTag)tags[tagIndex];
            if (original.Data.Span.SequenceEqual(placement.Artifact.Bytecode.Span))
                return original;
            var replacement = new DoInitActionTag(
                original.Metadata,
                spriteId,
                placement.Artifact.Bytecode);
            tags[tagIndex] = replacement;
            changes.Add(new PendingChange(
                Avm1SwfChangeKind.ReplaceTag,
                TagCode.DoInitAction,
                spriteId,
                original,
                replacement,
                $"Replace DoInitAction for {placement.Artifact.Source.Name}."));
            return replacement;
        }

        var insertIndex = spriteIndex + 1;
        var inserted = new DoInitActionTag(
            new TagMetadata(TagCode.DoInitAction, 0, 0),
            spriteId,
            placement.Artifact.Bytecode);
        tags.Insert(insertIndex, inserted);
        changes.Add(new PendingChange(
            Avm1SwfChangeKind.InsertTag,
            TagCode.DoInitAction,
            spriteId,
            OriginalTag: null,
            inserted,
            $"Insert DoInitAction for {placement.Artifact.Source.Name}."));
        return inserted;
    }

    private static DoActionTag? LinkDoAction(
        List<Tag> tags,
        Avm1SwfPlacement placement,
        List<PendingChange> changes,
        List<Avm1SwfLinkDiagnostic> diagnostics)
    {
        if (placement.ReplaceTagIndex is { } replaceIndex)
        {
            if (tags[replaceIndex] is not DoActionTag original)
            {
                AddError(
                    diagnostics,
                    "AVM1LNK010",
                    replaceIndex,
                    $"Tag {replaceIndex} is not a DoAction tag.");
                return null;
            }
            if (original.Data.Span.SequenceEqual(placement.Artifact.Bytecode.Span))
                return original;
            var replacement = new DoActionTag(
                original.Metadata,
                placement.Artifact.Bytecode);
            tags[replaceIndex] = replacement;
            changes.Add(new PendingChange(
                Avm1SwfChangeKind.ReplaceTag,
                TagCode.DoAction,
                SpriteId: 0,
                original,
                replacement,
                $"Replace frame action with {placement.Artifact.Source.Name}."));
            return replacement;
        }

        var insertIndex = FindFrameEnd(tags, placement.FrameIndex);
        if (insertIndex < 0)
        {
            AddError(
                diagnostics,
                "AVM1LNK011",
                -1,
                $"Destination SWF has no frame {placement.FrameIndex}.");
            return null;
        }
        var inserted = new DoActionTag(
            new TagMetadata(TagCode.DoAction, 0, 0),
            placement.Artifact.Bytecode);
        tags.Insert(insertIndex, inserted);
        changes.Add(new PendingChange(
            Avm1SwfChangeKind.InsertTag,
            TagCode.DoAction,
            SpriteId: 0,
            OriginalTag: null,
            inserted,
            $"Insert frame action for {placement.Artifact.Source.Name} in frame " +
            $"{placement.FrameIndex}."));
        return inserted;
    }

    private static void EnsureExportAsset(
        List<Tag> tags,
        ushort spriteId,
        string exportName,
        List<PendingChange> changes,
        List<Avm1SwfLinkDiagnostic> diagnostics)
    {
        var collidingName = tags
            .OfType<ExportAssetsTag>()
            .SelectMany(tag => tag.Assets)
            .FirstOrDefault(asset =>
                asset.Name == exportName && asset.Id != spriteId);
        if (collidingName is not null)
        {
            AddError(
                diagnostics,
                "AVM1LNK012",
                -1,
                $"Export name {exportName} already identifies sprite {collidingName.Id}.");
            return;
        }

        var matches = tags
            .Select((tag, tagIndex) => (tag, tagIndex))
            .Where(item => item.tag is ExportAssetsTag)
            .SelectMany(item => ((ExportAssetsTag)item.tag).Assets.Select(
                (asset, assetIndex) => (item.tagIndex, assetIndex, asset)))
            .Where(item => item.asset.Id == spriteId)
            .ToArray();
        if (matches.Length > 1)
        {
            AddError(
                diagnostics,
                "AVM1LNK013",
                matches[1].tagIndex,
                $"Sprite {spriteId} has multiple ExportAssets entries.");
            return;
        }
        if (matches.Length == 1)
        {
            var match = matches[0];
            if (match.asset.Name == exportName)
                return;
            var original = (ExportAssetsTag)tags[match.tagIndex];
            var assets = original.Assets
                .Select(asset => new AssetReference(asset.Id, asset.Name))
                .ToArray();
            assets[match.assetIndex].Name = exportName;
            var replacement = new ExportAssetsTag(original.Metadata, assets);
            tags[match.tagIndex] = replacement;
            changes.Add(new PendingChange(
                Avm1SwfChangeKind.UpdateExportAssets,
                TagCode.ExportAssets,
                spriteId,
                original,
                replacement,
                $"Change sprite {spriteId} export name to {exportName}."));
            return;
        }

        var exportIndex = tags.FindIndex(tag => tag is ExportAssetsTag);
        if (exportIndex >= 0)
        {
            var original = (ExportAssetsTag)tags[exportIndex];
            var assets = original.Assets
                .Select(asset => new AssetReference(asset.Id, asset.Name))
                .Append(new AssetReference(spriteId, exportName))
                .ToArray();
            var replacement = new ExportAssetsTag(original.Metadata, assets);
            tags[exportIndex] = replacement;
            changes.Add(new PendingChange(
                Avm1SwfChangeKind.UpdateExportAssets,
                TagCode.ExportAssets,
                spriteId,
                original,
                replacement,
                $"Add export {exportName} for sprite {spriteId}."));
            return;
        }

        var spriteIndex = tags.FindIndex(tag =>
            tag is DefineSpriteTag sprite && sprite.Id == spriteId);
        var insertIndex = spriteIndex >= 0 ? spriteIndex : 0;
        var inserted = new ExportAssetsTag(
            new TagMetadata(TagCode.ExportAssets, 0, 0),
            [new AssetReference(spriteId, exportName)]);
        tags.Insert(insertIndex, inserted);
        changes.Add(new PendingChange(
            Avm1SwfChangeKind.InsertTag,
            TagCode.ExportAssets,
            spriteId,
            OriginalTag: null,
            inserted,
            $"Insert export {exportName} for sprite {spriteId}."));
    }

    private static Avm1SwfChange[] CreateChanges(
        IReadOnlyList<Tag> resultTags,
        IReadOnlyList<PendingChange> changes) =>
        changes.Select(change => new Avm1SwfChange(
            change.Kind,
            change.TagCode,
            FindTagIndex(
                resultTags,
                ResolveFinalTag(change.ResultTag, changes)),
            change.SpriteId,
            change.Description)).ToArray();

    private static Avm1LinkedSourceMap CreateSourceMap(
        ShockwaveFlashHeader header,
        IReadOnlyList<Tag> resultTags,
        IReadOnlyList<PendingChange> changes,
        IReadOnlyList<PendingSourceMap> pendingSourceMaps)
    {
        if (pendingSourceMaps.Count == 0)
            return Avm1LinkedSourceMap.Empty;

        var encodedTags = EncodeTagRanges(resultTags, header.Version);
        var sites = new Avm1LinkedSourceMapSite[pendingSourceMaps.Count];
        for (var i = 0; i < pendingSourceMaps.Count; i++)
        {
            var pending = pendingSourceMaps[i];
            var resultTag = resultTags.Any(tag =>
                ReferenceEquals(tag, pending.ResultTag))
                ? pending.ResultTag
                : ResolveFinalTag(pending.ResultTag, changes);
            var encoded = FindEncodedTag(encodedTags, resultTag);
            var tagHeaderLength = GetTagHeaderLength(encoded.Bytes);
            var actionDataOffset = pending.Placement.Kind switch
            {
                Avm1SwfPlacementKind.DoInitAction => 2,
                Avm1SwfPlacementKind.DoAction => 0,
                _ => throw new InvalidOperationException(
                    $"Placement kind {pending.Placement.Kind} has no action data.")
            };
            var actionStreamOffset = checked(
                encoded.Range.TagStreamOffset +
                tagHeaderLength +
                actionDataOffset);
            var bytecode = pending.Placement.Artifact.Bytecode;
            var encodedActionOffset = checked(tagHeaderLength + actionDataOffset);
            if (encodedActionOffset + bytecode.Length > encoded.Bytes.Length ||
                !encoded.Bytes.AsSpan(encodedActionOffset, bytecode.Length)
                    .SequenceEqual(bytecode.Span))
            {
                throw new InvalidOperationException(
                    $"Linked {pending.Placement.Kind} tag does not contain the " +
                    $"compiled bytes for {pending.Placement.Artifact.Source.Name}.");
            }

            var tagCode = pending.Placement.Kind is Avm1SwfPlacementKind.DoInitAction
                ? TagCode.DoInitAction
                : TagCode.DoAction;
            sites[i] = new Avm1LinkedSourceMapSite(
                new Avm1LinkedSourceMapPlacement(
                    pending.PlacementIndex,
                    pending.Placement.Kind,
                    encoded.Range.TagIndex,
                    tagCode,
                    pending.Placement.Kind is Avm1SwfPlacementKind.DoInitAction
                        ? GetSpriteId(pending.Placement)
                        : (ushort)0,
                    encoded.Range.TagStreamOffset,
                    tagHeaderLength,
                    actionDataOffset,
                    actionStreamOffset,
                    bytecode.Length),
                pending.Placement.Artifact.SourceMap);
        }

        var headerWriter = new MemoryWriter();
        header.Encode(headerWriter);
        return Avm1LinkedSourceMap.Build(
            sites,
            checked(8 + headerWriter.Position));
    }

    private static int GetTagHeaderLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            throw new InvalidOperationException("An encoded SWF tag has no header.");
        var header = (ushort)(bytes[0] | bytes[1] << 8);
        var length = (header & 0x3f) == 0x3f ? 6 : 2;
        if (bytes.Length < length)
            throw new InvalidOperationException("An encoded SWF tag header is truncated.");
        return length;
    }

    private static Avm1SwfPatchReport CreatePatchReport(
        ShockwaveFlashHeader header,
        IReadOnlyList<Tag> originalTags,
        IReadOnlyList<Tag> resultTags,
        IReadOnlyList<PendingChange> changes)
    {
        var originalRanges = EncodeTagRanges(originalTags, header.Version);
        var resultRanges = EncodeTagRanges(resultTags, header.Version);
        var originalFile = new ShockwaveFlashFile(header, originalTags.ToList())
            .Assemble().ToArray();
        var resultFile = new ShockwaveFlashFile(header, resultTags.ToList())
            .Assemble().ToArray();
        var physicalChanges = CollapsePhysicalChanges(changes);
        var ranges = physicalChanges.Select(change =>
        {
            var result = FindEncodedTag(resultRanges, change.ResultTag);
            var original = change.OriginalTag is null
                ? (EncodedTag?)null
                : FindEncodedTag(originalRanges, change.OriginalTag);
            return new Avm1SwfPatchRange(
                change.Kind,
                change.TagCode,
                change.SpriteId,
                original?.Range,
                result.Range,
                original is { } prior && prior.Bytes.AsSpan().SequenceEqual(result.Bytes),
                change.Description);
        }).ToArray();

        return new Avm1SwfPatchReport(
            header.Compression,
            originalFile.Length,
            resultFile.Length,
            ComputeSha256(originalFile),
            ComputeSha256(resultFile),
            ranges);
    }

    private static PendingChange[] CollapsePhysicalChanges(
        IReadOnlyList<PendingChange> changes)
    {
        var result = new List<PendingChange>(changes.Count);
        foreach (var change in changes)
        {
            var priorIndex = result.FindIndex(prior =>
                ReferenceEquals(prior.ResultTag, change.OriginalTag));
            if (priorIndex < 0)
            {
                result.Add(change);
                continue;
            }

            var prior = result[priorIndex];
            result[priorIndex] = new PendingChange(
                prior.Kind,
                change.TagCode,
                prior.SpriteId == change.SpriteId ? prior.SpriteId : (ushort)0,
                prior.OriginalTag,
                change.ResultTag,
                prior.Description + " " + change.Description);
        }
        return result.ToArray();
    }

    private static Tag ResolveFinalTag(
        Tag resultTag,
        IReadOnlyList<PendingChange> changes)
    {
        var current = resultTag;
        for (var iteration = 0; iteration < changes.Count; iteration++)
        {
            var next = changes.FirstOrDefault(change =>
                ReferenceEquals(change.OriginalTag, current));
            if (next.ResultTag is null)
                return current;
            current = next.ResultTag;
        }
        throw new InvalidOperationException("A linked SWF tag replacement chain is cyclic.");
    }

    private static EncodedTag[] EncodeTagRanges(
        IReadOnlyList<Tag> tags,
        byte swfVersion)
    {
        var result = new EncodedTag[tags.Count];
        var offset = 0;
        for (var index = 0; index < tags.Count; index++)
        {
            var writer = new MemoryWriter();
            Tag.EncodeCollection(writer, [tags[index]], swfVersion);
            var bytes = writer.WrittenMemory.ToArray();
            var range = new Avm1SwfTagByteRange(
                index,
                offset,
                bytes.Length,
                ComputeSha256(bytes));
            result[index] = new EncodedTag(tags[index], range, bytes);
            offset += bytes.Length;
        }
        return result;
    }

    private static EncodedTag FindEncodedTag(
        IReadOnlyList<EncodedTag> tags,
        Tag expected)
    {
        for (var index = 0; index < tags.Count; index++)
        {
            if (ReferenceEquals(tags[index].Tag, expected))
                return tags[index];
        }
        throw new InvalidOperationException("A linked SWF tag is missing from its encoded stream.");
    }

    private static int FindTagIndex(IReadOnlyList<Tag> tags, Tag expected)
    {
        for (var index = 0; index < tags.Count; index++)
        {
            if (ReferenceEquals(tags[index], expected))
                return index;
        }
        throw new InvalidOperationException("A linked SWF tag is missing from the result.");
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private readonly record struct PendingChange(
        Avm1SwfChangeKind Kind,
        TagCode TagCode,
        ushort SpriteId,
        Tag? OriginalTag,
        Tag ResultTag,
        string Description);

    private readonly record struct PendingSourceMap(
        int PlacementIndex,
        Avm1SwfPlacement Placement,
        Tag ResultTag);

    private readonly record struct EncodedTag(
        Tag Tag,
        Avm1SwfTagByteRange Range,
        byte[] Bytes);

    private static int FindFrameEnd(List<Tag> tags, int frameIndex)
    {
        if (frameIndex < 0)
            return -1;
        var frame = 0;
        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i] is not ShowFrameTag)
                continue;
            if (frame == frameIndex)
                return i;
            frame++;
        }
        return -1;
    }

    private static ushort GetSpriteId(Avm1SwfPlacement placement) =>
        placement.SpriteId != 0
            ? placement.SpriteId
            : placement.Artifact.Source.Origin.SpriteId;

    private static bool HasErrors(
        IReadOnlyList<Avm1SwfLinkDiagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity is Avm1CompilationDiagnosticSeverity.Error);

    private static void AddError(
        List<Avm1SwfLinkDiagnostic> diagnostics,
        string code,
        int tagIndex,
        string message) =>
        diagnostics.Add(new Avm1SwfLinkDiagnostic(
            code,
            Avm1CompilationDiagnosticSeverity.Error,
            tagIndex,
            message));
}
