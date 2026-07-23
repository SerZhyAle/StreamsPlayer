using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamBitrateTests
{
    [Theory]
    [InlineData("128", 128)]
    [InlineData("128 kbps", 128)]
    [InlineData("320k", 320)]
    [InlineData("1.5 Mbps", 1500)]
    [InlineData("96kbit/s", 96)]
    public void TryParseKbps_InterpretsKnownClaims(string raw, int expected)
    {
        Assert.True(StreamBitrate.TryParseKbps(raw, out var kbps));
        Assert.Equal(expected, kbps);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("high")]
    [InlineData("kbps")]
    public void TryParseKbps_RejectsMissingOrMalformed(string? raw)
    {
        Assert.False(StreamBitrate.TryParseKbps(raw, out var kbps));
        Assert.Equal(0, kbps);
    }

    [Theory]
    [InlineData("128", 128, true)]
    [InlineData("96", 128, false)]
    [InlineData(null, 128, false)]
    [InlineData("garbage", 1, false)]
    public void MeetsMinimum_ExcludesUnknownUnderActiveMinimum(string? raw, int minimum, bool expected)
    {
        Assert.Equal(expected, StreamBitrate.MeetsMinimum(raw, minimum));
    }
}
