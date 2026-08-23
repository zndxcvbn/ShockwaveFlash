using ShockwaveFlash.Avm1;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Types;
using ShockwaveFlash.Types.Control;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1TimelineLayoutTests
{
    [Fact]
    public void Extracts_scene_offsets_and_frame_labels_from_a_swf()
    {
        var tag = new DefineSceneAndFrameLabelDataTag(
            new TagMetadata(TagCode.DefineSceneAndFrameLabelData, 0, 0),
            [
                new SceneOffset(0, "Scene 1"),
                new SceneOffset(10, "Scene 2")
            ],
            [
                new FrameLabel(2, "intro"),
                new FrameLabel(13, "middle")
            ]);
        var swf = new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                Version: 10,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 20),
            [tag]);

        var layout = Avm1TimelineLayout.FromSwf(swf).ShouldNotBeNull();

        layout.FrameCount.ShouldBe((uint)20);
        layout.Scenes.ShouldBe([
            new Avm1TimelineScene("Scene 1", 0),
            new Avm1TimelineScene("Scene 2", 10)
        ]);
        layout.TryGetSceneByFrameOffset(10, out var scene).ShouldBeTrue();
        scene.Name.ShouldBe("Scene 2");
        layout.TryResolveFrame("Scene 2", 3, out var frame).ShouldBeTrue();
        frame.ShouldBe((uint)12);
        layout.TryResolveFrameLabel("Scene 2", "middle", out frame).ShouldBeTrue();
        frame.ShouldBe((uint)13);
        layout.TryResolveFrameLabel("Scene 1", "middle", out _).ShouldBeFalse();
    }

    [Fact]
    public void Returns_null_when_a_swf_has_no_scene_metadata()
    {
        var swf = new ShockwaveFlashFile(
            new ShockwaveFlashHeader(
                ShockwaveFlashCompression.None,
                Version: 7,
                FileLength: 0,
                FrameSize: new Rectangle(0, 0, 100, 100),
                FrameRate: Fixed8.Zero,
                FrameCount: 1),
            []);

        Avm1TimelineLayout.FromSwf(swf).ShouldBeNull();
    }
}
