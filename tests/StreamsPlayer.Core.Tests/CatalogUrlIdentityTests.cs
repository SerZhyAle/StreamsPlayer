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
}
