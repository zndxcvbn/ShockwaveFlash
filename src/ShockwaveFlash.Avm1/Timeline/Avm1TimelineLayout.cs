using System.Collections.ObjectModel;
using ShockwaveFlash.Tags.Control;

namespace ShockwaveFlash.Avm1;

public readonly record struct Avm1TimelineScene(
    string Name,
    uint FrameOffset);

public readonly record struct Avm1TimelineFrameLabel(
    string Name,
    uint FrameIndex);

public sealed class Avm1TimelineLayout
{
    private readonly Avm1TimelineScene[] _scenes;
    private readonly Avm1TimelineFrameLabel[] _frameLabels;
    private readonly ReadOnlyCollection<Avm1TimelineScene> _readOnlyScenes;
    private readonly ReadOnlyCollection<Avm1TimelineFrameLabel> _readOnlyFrameLabels;

    public Avm1TimelineLayout(
        uint frameCount,
        IEnumerable<Avm1TimelineScene> scenes,
        IEnumerable<Avm1TimelineFrameLabel>? frameLabels = null)
    {
        ArgumentNullException.ThrowIfNull(scenes);

        FrameCount = frameCount;
        _scenes = scenes.ToArray();
        _frameLabels = frameLabels?.ToArray() ?? [];
        Validate();
        _readOnlyScenes = Array.AsReadOnly(_scenes);
        _readOnlyFrameLabels = Array.AsReadOnly(_frameLabels);
    }

    public uint FrameCount { get; }

    public IReadOnlyList<Avm1TimelineScene> Scenes => _readOnlyScenes;

    public IReadOnlyList<Avm1TimelineFrameLabel> FrameLabels =>
        _readOnlyFrameLabels;

    public static Avm1TimelineLayout FromTag(
        DefineSceneAndFrameLabelDataTag tag,
        uint frameCount)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return new Avm1TimelineLayout(
            frameCount,
            tag.Scenes.Select(scene => new Avm1TimelineScene(
                scene.Name,
                scene.Offset)),
            tag.FrameLabels.Select(label => new Avm1TimelineFrameLabel(
                label.Label,
                label.FrameNum)));
    }

    public static Avm1TimelineLayout? FromSwf(ShockwaveFlashFile swf)
    {
        ArgumentNullException.ThrowIfNull(swf);
        var sceneTags = swf.Tags
            .OfType<DefineSceneAndFrameLabelDataTag>()
            .ToArray();
        return sceneTags.Length switch
        {
            0 => null,
            1 => FromTag(sceneTags[0], swf.Header.FrameCount),
            _ => throw new InvalidOperationException(
                "A SWF cannot define more than one scene and frame-label data tag.")
        };
    }

    public bool TryGetScene(
        string name,
        out Avm1TimelineScene scene)
    {
        ArgumentNullException.ThrowIfNull(name);
        scene = default;
        var found = false;
        foreach (var candidate in _scenes)
        {
            if (!candidate.Name.Equals(name, StringComparison.Ordinal))
                continue;
            if (found)
            {
                scene = default;
                return false;
            }
            scene = candidate;
            found = true;
        }
        return found;
    }

    public bool TryGetSceneByFrameOffset(
        uint frameOffset,
        out Avm1TimelineScene scene)
    {
        var low = 0;
        var high = _scenes.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var candidate = _scenes[middle];
            if (candidate.FrameOffset == frameOffset)
            {
                scene = candidate;
                return true;
            }
            if (candidate.FrameOffset < frameOffset)
                low = middle + 1;
            else
                high = middle - 1;
        }

        scene = default;
        return false;
    }

    public bool TryResolveFrame(
        string sceneName,
        uint oneBasedFrame,
        out uint frameIndex)
    {
        frameIndex = 0;
        if (oneBasedFrame == 0 || !TryGetScene(sceneName, out var scene))
            return false;

        var candidate = (ulong)scene.FrameOffset + oneBasedFrame - 1;
        var sceneEnd = GetSceneEndFrameOffset(scene.FrameOffset);
        if (candidate >= sceneEnd || candidate > uint.MaxValue)
            return false;

        frameIndex = (uint)candidate;
        return true;
    }

    public bool TryResolveFrameLabel(
        string sceneName,
        string labelName,
        out uint frameIndex)
    {
        ArgumentNullException.ThrowIfNull(labelName);
        frameIndex = 0;
        if (!TryGetScene(sceneName, out var scene))
            return false;

        var sceneEnd = GetSceneEndFrameOffset(scene.FrameOffset);
        var found = false;
        foreach (var label in _frameLabels)
        {
            if (label.FrameIndex < scene.FrameOffset ||
                label.FrameIndex >= sceneEnd ||
                !label.Name.Equals(labelName, StringComparison.Ordinal))
            {
                continue;
            }
            if (found)
            {
                frameIndex = 0;
                return false;
            }
            frameIndex = label.FrameIndex;
            found = true;
        }
        return found;
    }

    private uint GetSceneEndFrameOffset(uint frameOffset)
    {
        for (var i = 0; i < _scenes.Length; i++)
        {
            if (_scenes[i].FrameOffset != frameOffset)
                continue;
            return i + 1 < _scenes.Length
                ? _scenes[i + 1].FrameOffset
                : FrameCount;
        }
        return frameOffset;
    }

    private void Validate()
    {
        if (_scenes.Length != 0 && _scenes[0].FrameOffset != 0)
        {
            throw new ArgumentException(
                "The first timeline scene must start at frame offset zero.",
                "scenes");
        }

        for (var i = 0; i < _scenes.Length; i++)
        {
            var scene = _scenes[i];
            if (scene.Name is null)
                throw new ArgumentException("Scene names cannot be null.", "scenes");
            if (scene.FrameOffset >= FrameCount)
            {
                throw new ArgumentOutOfRangeException(
                    "scenes",
                    $"Scene {scene.Name} starts outside the {FrameCount} frame timeline.");
            }
            if (i != 0 && _scenes[i - 1].FrameOffset >= scene.FrameOffset)
            {
                throw new ArgumentException(
                    "Timeline scene offsets must be strictly increasing.",
                    "scenes");
            }
        }

        foreach (var label in _frameLabels)
        {
            if (label.Name is null)
                throw new ArgumentException(
                    "Frame-label names cannot be null.",
                    "frameLabels");
            if (label.FrameIndex >= FrameCount)
            {
                throw new ArgumentOutOfRangeException(
                    "frameLabels",
                    $"Frame label {label.Name} is outside the {FrameCount} frame timeline.");
            }
        }
    }
}
