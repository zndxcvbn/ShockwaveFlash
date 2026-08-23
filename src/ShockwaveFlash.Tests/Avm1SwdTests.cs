using ShockwaveFlash.Avm1.Compilation;
using ShockwaveFlash.Exceptions;
using Shouldly;

namespace ShockwaveFlash.Tests;

public sealed class Avm1SwdTests
{
    private const string DebugIdBytes = "00112233445566778899AABBCCDDEEFF";

    private const string GoldenBytes =
        "46574407" +
        "03000000" + DebugIdBytes +
        "00000000" +
        "01000000" +
        "01000000" +
        "4D61696E2E617300" +
        "74726163652831293B0A00" +
        "01000000" +
        "01000000" +
        "02000000" +
        "34120000" +
        "02000000" +
        "0100" +
        "0200" +
        "05000000" +
        "34120000" +
        "02" +
        "01" + "6900" +
        "02" + "76616C756500";

    [Fact]
    public void Codec_matches_the_known_swd7_binary_layout()
    {
        var debugId = new Guid(Convert.FromHexString(DebugIdBytes));
        var file = new Avm1SwdFile(
            Avm1SwdFile.CurrentVersion,
            [
                new Avm1SwdDebugIdRecord(debugId),
                new Avm1SwdSourceFileRecord(
                    moduleId: 1,
                    Avm1SwdSourceFileRecord.ActionScriptBitmap,
                    "Main.as",
                    "trace(1);\n"),
                new Avm1SwdOffsetMapRecord(
                    moduleId: 1,
                    line: 2,
                    swfByteOffset: 0x1234),
                new Avm1SwdBreakpointRecord(moduleId: 1, line: 2),
                new Avm1SwdRegisterMapRecord(
                    swfByteOffset: 0x1234,
                    [
                        new Avm1SwdRegisterName(1, "i"),
                        new Avm1SwdRegisterName(2, "value")
                    ])
            ]);
        var expected = Convert.FromHexString(GoldenBytes);

        var encoded = Avm1SwdCodec.Encode(file);
        var decoded = Avm1SwdCodec.Decode(expected);

        encoded.ToArray().ShouldBe(expected);
        decoded.Version.ShouldBe(Avm1SwdFile.CurrentVersion);
        decoded.Records.Count.ShouldBe(5);
        decoded.Records[0].ShouldBeOfType<Avm1SwdDebugIdRecord>()
            .Id.ShouldBe(debugId);
        var source = decoded.Records[1]
            .ShouldBeOfType<Avm1SwdSourceFileRecord>();
        source.ModuleId.ShouldBe((uint)1);
        source.Bitmap.ShouldBe(Avm1SwdSourceFileRecord.ActionScriptBitmap);
        source.Path.ShouldBe("Main.as");
        source.SourceText.ShouldBe("trace(1);\n");
        var offset = decoded.Records[2]
            .ShouldBeOfType<Avm1SwdOffsetMapRecord>();
        offset.ModuleId.ShouldBe((uint)1);
        offset.Line.ShouldBe((uint)2);
        offset.SwfByteOffset.ShouldBe((uint)0x1234);
        var breakpoint = decoded.Records[3]
            .ShouldBeOfType<Avm1SwdBreakpointRecord>();
        breakpoint.ModuleId.ShouldBe((ushort)1);
        breakpoint.Line.ShouldBe((ushort)2);
        var registers = decoded.Records[4]
            .ShouldBeOfType<Avm1SwdRegisterMapRecord>();
        registers.SwfByteOffset.ShouldBe((uint)0x1234);
        registers.Registers.ShouldBe([
            new Avm1SwdRegisterName(1, "i"),
            new Avm1SwdRegisterName(2, "value")
        ]);
        Avm1SwdCodec.Encode(decoded).ToArray().ShouldBe(expected);
    }

    [Theory]
    [InlineData("42414407")]
    [InlineData("46574405")]
    [InlineData("4657440704000000")]
    [InlineData("4657440703000000001122")]
    [InlineData("46574407000000000100000001000000FF0000")]
    public void Codec_rejects_malformed_or_unsupported_data(string hex)
    {
        Should.Throw<SwfException>(() =>
            Avm1SwdCodec.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Model_rejects_unencodable_strings_and_register_counts()
    {
        Should.Throw<ArgumentException>(() =>
            new Avm1SwdSourceFileRecord(1, 1, "bad\0path", string.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new Avm1SwdRegisterMapRecord(
                0,
                Enumerable.Range(0, 256).Select(index =>
                    new Avm1SwdRegisterName((byte)index, "value"))));
    }
}
