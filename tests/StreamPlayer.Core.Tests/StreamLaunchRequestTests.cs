using StreamPlayer.Core;

namespace StreamPlayer.Core.Tests;

public sealed class StreamLaunchRequestTests
{
    [Fact]
    public void Parse_EmptyArguments_ReturnsNone()
    {
        Assert.Equal(StreamLaunchTargetKind.None, StreamLaunchRequest.Parse([]).Kind);
    }

    [Fact]
    public void Parse_LaunchableUrl_ReturnsUrlTarget()
    {
        var request = StreamLaunchRequest.Parse(["--url", "https://example.test/live.m3u8"]);

        Assert.Equal(StreamLaunchTargetKind.Url, request.Kind);
        Assert.Equal("https://example.test/live.m3u8", request.Url);
    }

    [Fact]
    public void Parse_ChannelId_ReturnsChannelTarget()
    {
        var id = Guid.NewGuid();

        var request = StreamLaunchRequest.Parse(["--id", id.ToString()]);

        Assert.Equal(StreamLaunchTargetKind.ChannelId, request.Kind);
        Assert.Equal(id, request.ChannelId);
    }

    [Theory]
    [InlineData("--url", "file:///not-a-stream")]
    [InlineData("--id", "not-a-guid")]
    [InlineData("--unknown", "value")]
    public void Parse_InvalidOptionOrValue_ReturnsInvalid(string option, string value)
    {
        Assert.Equal(StreamLaunchTargetKind.Invalid, StreamLaunchRequest.Parse([option, value]).Kind);
    }

    [Fact]
    public void Parse_MissingValue_ReturnsInvalid()
    {
        Assert.Equal(StreamLaunchTargetKind.Invalid, StreamLaunchRequest.Parse(["--url"]).Kind);
    }
}
