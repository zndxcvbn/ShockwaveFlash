using ShockwaveFlash;
using ShockwaveFlash.Rendering;
using ShockwaveFlash.Tags;
using ShockwaveFlash.Tags.Control;
using ShockwaveFlash.Tags.Font;
using ShockwaveFlash.Types;
using ShockwaveFlash.Types.Font;
using ShockwaveFlash.Types.Shape;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class FontTests
{
    [Fact]
    public void DefineFont3_preserves_scaleform_extension_data_for_an_empty_font()
    {
        var extensionData = new byte[]
        {
            0xE0, 0x4C, 0xA8, 0x11, 0x10, 0x09, 0xE2, 0x03
        };
        var header = new ShockwaveFlashHeader(
            ShockwaveFlashCompression.None,
            8,
            0,
            new Rectangle(0, 0, 0, 0),
            new Fixed8(0),
            1);
        var font = new DefineFont3Tag(
            new TagMetadata(TagCode.DefineFont3, 0, 0),
            id: 3,
            name: "Compact Font\0",
            language: Language.Latin,
            layout: null,
            glyphs: [],
            flags: FontFlags.HasWideCodes,
            extensionData);
        var file = new ShockwaveFlashFile(
            header,
            [font, new EndTag(new TagMetadata(TagCode.End, 0, 0))]);

        var encoded = file.Assemble();
        var decoded = ShockwaveFlashFile.Disassemble(encoded);
        var decodedFont = decoded.Tags.OfType<DefineFont3Tag>().ShouldHaveSingleItem();

        decodedFont.ExtensionData.ToArray().ShouldBe(extensionData);
        decoded.Assemble().ToArray().ShouldBe(encoded.ToArray());
    }

    [Fact]
    public void DefineFont_v1_is_indexed_with_codes_from_DefineFontInfo()
    {
        var header = new ShockwaveFlashHeader(ShockwaveFlashCompression.None, 6, 0, new Rectangle(0, 0, 0, 0), new Fixed8(0), 1);

        IReadOnlyList<IReadOnlyList<ShapeRecord>> glyphs = [[new EndShapeRecord()], [new EndShapeRecord()]];
        var font = new DefineFontTag(new TagMetadata(TagCode.DefineFont, 0, 0), 5, [4, 4], glyphs);
        var info = new DefineFontInfoTag(new TagMetadata(TagCode.DefineFontInfo, 0, 0), 5, "Font", default, [65, 66]);

        var file = new ShockwaveFlashFile(header, [font, info]);

        var resolved = new SwfRenderer(file).ResolveFont(5);

        resolved.ShouldNotBeNull();
        resolved.Glyphs.Count.ShouldBe(2);
        resolved.EmSquare.ShouldBe(1024f);
        resolved.IndexForCode(65).ShouldBe(0);
        resolved.IndexForCode(66).ShouldBe(1);
        resolved.IndexForCode(99).ShouldBe(-1);
    }
}
