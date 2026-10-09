using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// Audit 26.1010.0106 A3: the address in a shareable failure report carries neither the listening right of
/// an exchange broadcast (SP-0201 requirement 5) nor the Windows user name of a stored local path.
/// </summary>
public sealed class CatalogUrlIdentityCapabilityRedactionTests
{
    private const string BroadcastId = "ICEiIyQlJicoKSorLC0uLw";

    [Theory]
    [InlineData(
        "https://exchange.example.net:44022/v2/b/" + BroadcastId + "/stream",
        "https://exchange.example.net:44022/v2/b/[REDACTED]/stream")]
    [InlineData(
        "https://exchange.example.net/b/" + BroadcastId + "/http",
        "https://exchange.example.net/b/[REDACTED]/http")]
    [InlineData(
        "fmsx://exchange.example.net:44022/b/" + BroadcastId + "/http",
        "fmsx://exchange.example.net:44022/b/[REDACTED]/http")]
    public void Redact_MasksTheBroadcastIdOfARelayOrTunnelAddress(string url, string expected)
    {
        var redacted = CatalogUrlIdentity.Redact(url);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain(BroadcastId, redacted);
    }

    [Fact]
    public void Redact_MasksTheBroadcastIdOfAnAddressThatDoesNotParse()
    {
        var redacted = CatalogUrlIdentity.Redact("https://exchange.example.net/v2/b/" + BroadcastId + "/stream x");

        Assert.DoesNotContain(BroadcastId, redacted);
    }

    [Fact]
    public void FailureReport_NeverCarriesABroadcastId()
    {
        var report = new FailureReport(
            "26.1010.0106",
            new DateTimeOffset(2026, 10, 10, 1, 6, 0, TimeSpan.Zero),
            "Kitchen",
            "https://exchange.example.net:44022/v2/b/" + BroadcastId + "/stream",
            MediaKind.Audio,
            PlaybackErrorCategory.Network);

        var text = FailureReportFormatter.Format(report);

        Assert.DoesNotContain(BroadcastId, text);
        Assert.Contains("/v2/b/[REDACTED]/stream", text);
    }

    [Fact]
    public void Redact_LeavesAnOrdinaryLanAddressAsItWas()
    {
        Assert.Equal(
            "http://192.168.1.97:8768/live-audio.aac",
            CatalogUrlIdentity.Redact("http://192.168.1.97:8768/live-audio.aac"));
    }

    [Theory]
    [InlineData(@"C:\Users\ann\a.mp3", "a.mp3")]
    [InlineData(@"C:\Users\ann\Music\", "[REDACTED]")]
    [InlineData(@"\\srv\share\ann\song one.mp3", "song one.mp3")]
    [InlineData("file:///C:/Users/ann/a.mp3", "a.mp3")]
    public void Redact_ReducesALocalPathToTheBareFileName(string stored, string expected)
    {
        var redacted = CatalogUrlIdentity.Redact(stored);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("ann", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", redacted, StringComparison.OrdinalIgnoreCase);
    }
}
