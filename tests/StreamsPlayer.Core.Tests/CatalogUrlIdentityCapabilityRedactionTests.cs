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
        "fmsx://exchange.example.net:44022/b/" + BroadcastId + "/http",
        "fmsx://exchange.example.net:44022/b/[REDACTED]/http")]
    public void Redact_MasksTheBroadcastIdOfARelayOrTunnelAddress(string url, string expected)
    {
        var redacted = CatalogUrlIdentity.Redact(url);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain(BroadcastId, redacted);
    }

    /// <summary>Re-audit D1: only the contract's shapes are capability addresses, not any "/b/" segment.</summary>
    [Theory]
    [InlineData("http://h/radio/b/live.mp3", "http://h/radio/b/live.mp3")]
    [InlineData("https://h/b/news", "https://h/b/news")]
    [InlineData("https://exchange.example.net/b/" + BroadcastId + "/http", "https://exchange.example.net/b/" + BroadcastId + "/http")]
    [InlineData("http://h/x?next=/b/news", "http://h/x?next=/b/news")]
    public void Redact_LeavesAnOrdinaryAddressWithABSegmentAsItWas(string url, string expected) =>
        Assert.Equal(expected, CatalogUrlIdentity.Redact(url));

    [Theory]
    [InlineData("https://h/radio/b/live.mp3 x")]
    [InlineData("https://h/b/news x")]
    public void Redact_LeavesAnUnparsableOrdinaryAddressWithABSegmentAlone(string text)
    {
        // Not the tunnel or relay shape, so nothing is masked even when the text does not parse as a URI.
        Assert.DoesNotContain("[REDACTED]", CatalogUrlIdentity.Redact(text));
    }

    /// <summary>Re-audit D2: the query and fragment of a capability address are masked as well as its path.</summary>
    [Fact]
    public void Redact_MasksTheBroadcastIdInTheQueryAndFragmentOfACapabilityAddress()
    {
        var redacted = CatalogUrlIdentity.Redact(
            "https://exchange.example.net/v2/b/" + BroadcastId + "/stream?resume=/b/" + BroadcastId + "#/b/" + BroadcastId);

        Assert.DoesNotContain(BroadcastId, redacted);
        Assert.StartsWith("https://exchange.example.net/v2/b/[REDACTED]/stream?resume=/b/[REDACTED]#/b/[REDACTED]", redacted);
    }

    [Fact]
    public void RedactCapabilityAddresses_AnchorsToTheContractShapesInFreeText()
    {
        var text = "open fmsx://h:44022/b/" + BroadcastId + "/http and https://h/v2/b/" + BroadcastId + "/stream but not http://h/radio/b/live.mp3";

        var redacted = ExchangeDiagnosticRedactor.RedactCapabilityAddresses(text);

        Assert.Equal(
            "open fmsx://h:44022/b/[REDACTED]/http and https://h/v2/b/[REDACTED]/stream but not http://h/radio/b/live.mp3",
            redacted);
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

    /// <summary>
    /// Re-audit D3: a last segment that is not a media file is the user name or a share name, and is withheld;
    /// an escaped control character never reaches the one-line report field.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\ann")]
    [InlineData(@"C:\Users\ann.lee")]
    [InlineData(@"\\srv\home\ann")]
    [InlineData(@"\\srv\share")]
    [InlineData("file:///C:/Users/ann")]
    public void Redact_WithholdsALocalPathWhoseLastSegmentIsNotAMediaFile(string stored) =>
        Assert.Equal("[REDACTED]", CatalogUrlIdentity.Redact(stored));

    [Fact]
    public void Redact_StripsControlCharactersFromAReturnedLocalFileName()
    {
        var redacted = CatalogUrlIdentity.Redact("file:///C:/Users/ann/a%0Ab%09c.mp3");

        Assert.Equal("abc.mp3", redacted);
        Assert.DoesNotContain(redacted, char.IsControl);
    }
}
