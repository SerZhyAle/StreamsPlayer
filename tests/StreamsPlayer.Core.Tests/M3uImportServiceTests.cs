using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class M3uImportServiceTests
{
    [Fact]
    public void DecodeUtf8_ReadsPlainUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("#EXTM3U\nhttps://a.test/live\n");
        Assert.Equal("#EXTM3U\nhttps://a.test/live\n", M3uImportService.DecodeUtf8(bytes));
    }

    [Fact]
    public void DecodeUtf8_StripsLeadingBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("#EXTM3U"))
            .ToArray();

        var text = M3uImportService.DecodeUtf8(bytes);

        Assert.StartsWith("#EXTM3U", text);
        Assert.Equal('#', text[0]);
    }

    [Fact]
    public void DecodeUtf8_ThrowsOnInvalidEncoding()
    {
        // 0x80 is a stray continuation byte — not valid UTF-8.
        var bytes = new byte[] { 0x23, 0x80, 0x81 };
        Assert.Throws<DecoderFallbackException>(() => M3uImportService.DecodeUtf8(bytes));
    }
}
