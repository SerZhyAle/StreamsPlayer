using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class CatalogUrlIdentityTests
{
    [Fact]
    public void Normalize_IsIdempotent()
    {
        const string url = "HTTPS://Example.COM:443/Live/Feed.m3u8?Q=1";
        var once = CatalogUrlIdentity.Normalize(url);
        var twice = CatalogUrlIdentity.Normalize(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Normalize_LowercasesSchemeAndHostButKeepsPathCase()
    {
        var result = CatalogUrlIdentity.Normalize("HTTPS://Example.COM/Live/Feed.m3u8?Token=AbC");
        Assert.Equal("https://example.com/Live/Feed.m3u8?Token=AbC", result);
    }

    [Fact]
    public void IsHidden_MatchesReAddedCatalogUrl()
    {
        // A catalog refresh re-adds the exact URL; the hidden entry must still match.
        string[] hidden = ["https://cdn.example/stream.m3u8"];
        Assert.True(CatalogUrlIdentity.IsHidden(hidden, "https://cdn.example/stream.m3u8"));
        Assert.True(CatalogUrlIdentity.IsHidden(hidden, "HTTPS://CDN.Example/stream.m3u8"));
        Assert.False(CatalogUrlIdentity.IsHidden(hidden, "https://cdn.example/other.m3u8"));
    }

    [Fact]
    public void Redact_StripsUserInfoCredentials()
    {
        var redacted = CatalogUrlIdentity.Redact("https://alice:s3cr3t@host.example/live.m3u8");
        Assert.DoesNotContain("alice", redacted);
        Assert.DoesNotContain("s3cr3t", redacted);
        Assert.Contains("host.example/live.m3u8", redacted);
    }

    [Fact]
    public void Redact_MasksCredentialQueryValuesButKeepsOthers()
    {
        var redacted = CatalogUrlIdentity.Redact("https://host.example/live?token=ABC123&region=eu");
        Assert.Contains("token=***", redacted);
        Assert.DoesNotContain("ABC123", redacted);
        Assert.Contains("region=eu", redacted);
    }

    [Fact]
    public void Redact_LeavesUnparsableInputTrimmed()
    {
        Assert.Equal("not a url", CatalogUrlIdentity.Redact("  not a url  "));
    }

    // SP-0123: the log sink's redactor. A clean line must come back byte-identical - SP-0040 keeps full
    // addresses for measurement - and only the secret itself may change in a dirty one.
    [Theory]
    [InlineData("STATS | fps=25 | buffer=15000", "STATS | fps=25 | buffer=15000")]
    [InlineData("PLAY | url=https://CDN.Example/Live%20TV/a.m3u8?region=eu | ok",
                "PLAY | url=https://CDN.Example/Live%20TV/a.m3u8?region=eu | ok")]
    [InlineData("PLAY | url=rtsp://user:pass@host:554/x | ok", "PLAY | url=rtsp://host:554/x | ok")]
    [InlineData("PLAY | url=rtsp://admin@cam/x", "PLAY | url=rtsp://cam/x")]
    [InlineData("url=https://host/s?token=abc&id=1", "url=https://host/s?token=[REDACTED]&id=1")]
    [InlineData("url=https://host/s?id=1&API_KEY=z&ApiKey=q#frag", "url=https://host/s?id=1&API_KEY=z&ApiKey=[REDACTED]#frag")]
    [InlineData("open failed: 'http://u:p@h/a' then \"rtmp://v:q@g/b\"", "open failed: 'http://h/a' then \"rtmp://g/b\"")]
    [InlineData("url=http://[::1]:8080/x", "url=http://[::1]:8080/x")]
    [InlineData("file:///C:/Users/me/list.m3u", "file:///C:/Users/me/list.m3u")]
    public void RedactText_RemovesOnlyTheCredentials(string input, string expected)
    {
        Assert.Equal(expected, CatalogUrlIdentity.RedactText(input));
    }

    // A compacted or truncated log can cut a line inside "user:pass@host"; the fragment must not survive.
    [Fact]
    public void RedactText_RedactsAnAuthorityCutInsideItsUserInfo()
    {
        Assert.Equal("tail of rtsp://[REDACTED]", CatalogUrlIdentity.RedactText("tail of rtsp://user:hunter"));
    }
}
