using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0174, DIAGNOSTIC-REPORT rule 3: every secret address shape the audit named - the ordinary IPTV and
/// signed-CDN forms an imported playlist hands the player - comes out of both redactors with no secret
/// left. <see cref="CatalogUrlIdentity.Redact"/> feeds the failure report, <see cref="CatalogUrlIdentity.RedactText"/>
/// the log sink and the archive; both are gated here because a shape fixed in one and not the other is a
/// shape still leaking.
/// </summary>
public sealed class CatalogUrlIdentityAuditedShapeTests
{
    [Theory]
    // Xtream-style IPTV: the account lives in the path.
    [InlineData("http://panel.example:8080/live/alice/TopSecretPass/12345.ts")]
    [InlineData("http://panel.example:8080/movie/alice/TopSecretPass/9987.mkv")]
    [InlineData("http://panel.example:8080/SERIES/alice/TopSecretPass/s01e02.mkv")]
    // Signed-CDN query parameters.
    [InlineData("https://cdn.example/v/a.m3u8?X-Amz-Signature=SIG007&X-Amz-Date=20260929")]
    [InlineData("https://cdn.example/v/a.m3u8?X-Amz-Credential=AKIA9/20260929/eu-west-1/s3/aws4_request")]
    [InlineData("https://cdn.example/v/a.m3u8?X-Amz-Security-Token=TOKN007&v=1")]
    [InlineData("https://edge.example/hls/a.m3u8?wmsAuthSign=SIGN2")]
    [InlineData("https://edge.example/hls/a.m3u8?hdnts=st=17~exp=99~acl=/hls/*~hmac=9f")]
    [InlineData("https://edge.example/hls/a.m3u8?hdnea=exp=99~acl=*~hmac=1a")]
    [InlineData("https://cloudfront.example/a.m3u8?Policy=eyJSdWxlIiA6&Key-Pair-Id=APKAJ&Signature=xx&_=_")]
    [InlineData("https://api.example/v1/list?api_key=AKIA9KEY&limit=10")]
    [InlineData("https://sso.example/token?client_secret=CS007&grant_type=refresh")]
    [InlineData("https://sso.example/token?refresh_token=RT/0042&scope=read")]
    // The HTML-escaped separator: an exception message or an embedded player URL keeps &amp;token=.
    [InlineData("https://host.example/p?a=1&amp;token=TOPSECRET")]
    // A userinfo password containing '/', '?' or '#' makes the whole address unparsable - and leaks
    // piecewise through the free-text redactor's authority scan when nobody looks for a later '@'.
    [InlineData("http://alice:Top/Secret@camera.example/stream1")]
    [InlineData("http://alice:Top?Secret@camera.example/stream1")]
    [InlineData("http://alice:Top#Secret@camera.example/stream1")]
    public void Redact_LeavesNoSecretFromAnyAuditedShape(string url)
    {
        var redacted = CatalogUrlIdentity.Redact(url);

        Assert.DoesNotContain("TopSecretPass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Top", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("SIG007", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA9", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKN007", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGN2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hmac=9f", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hmac=1a", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJSdWxlIiA", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("APKAJ", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("CS007", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("RT/0042", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("TOPSECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://panel.example:8080/live/alice/TopSecretPass/12345.ts",
                "http://panel.example:8080/live/[REDACTED]/[REDACTED]/12345.ts")]
    [InlineData("https://cdn.example/v/a.m3u8?X-Amz-Signature=SIG007&X-Amz-Date=20260929",
                "https://cdn.example/v/a.m3u8?X-Amz-Signature=***&X-Amz-Date=20260929")]
    [InlineData("https://host.example/p?a=1&amp;token=TOPSECRET",
                "https://host.example/p?a=1&amp;token=***")]
    public void Redact_ReplacesOnlyTheSecretParts(string url, string expected)
    {
        Assert.Equal(expected, CatalogUrlIdentity.Redact(url));
    }

    // The log sink's redactor sees the same shapes inside a line, with [REDACTED] rather than ***.
    [Theory]
    [InlineData(
        "[Diag] PLAYER OPEN | url=http://panel.example:8080/live/alice/TopSecretPass/12345.ts?play=1 | backend=libvlc",
        "[Diag] PLAYER OPEN | url=http://panel.example:8080/live/[REDACTED]/[REDACTED]/12345.ts?play=1 | backend=libvlc")]
    [InlineData(
        "err: 'https://cdn.example/v/a.m3u8?X-Amz-Signature=SIG007&v=1' failed",
        "err: 'https://cdn.example/v/a.m3u8?X-Amz-Signature=[REDACTED]&v=1' failed")]
    [InlineData(
        "fallback https://edge.example/hls/a.m3u8?wmsAuthSign=SIGN2 timeout",
        "fallback https://edge.example/hls/a.m3u8?wmsAuthSign=[REDACTED] timeout")]
    // An HTML-escaped pair separator: the name after &amp; must match the secret list too.
    [InlineData(
        "open https://host.example/p?a=1&amp;token=TOPSECRET&amp;b=2 end",
        "open https://host.example/p?a=1&amp;token=[REDACTED]&amp;b=2 end")]
    // A password cut by a '/' inside it: everything up to the host after the later '@' is userinfo.
    [InlineData(
        "url=http://alice:Top/Secret@camera.example/stream1 ok",
        "url=http://camera.example/stream1 ok")]
    public void RedactText_RedactsTheSameShapesInsideALogLine(string input, string expected)
    {
        Assert.Equal(expected, CatalogUrlIdentity.RedactText(input));
    }

    // A password containing '?' or '#' splits the authority the same way; the fragment-shaped piece of
    // the password must not survive as a fake query.
    [Theory]
    [InlineData("http://alice:Top?Secret@camera.example/stream1")]
    [InlineData("http://alice:Top#Secret@camera.example/stream1")]
    public void RedactText_ReplacesAPasswordContainingAQueryOrFragmentDelimiter(string url)
    {
        var redacted = CatalogUrlIdentity.RedactText(url);

        Assert.Equal("http://camera.example/stream1", redacted);
    }

    // The shared rule (SP-0167 R3 reuses it): an address any redactor would touch is an address an
    // export or a launch argument must refuse.
    [Theory]
    [InlineData("http://panel.example:8080/live/alice/TopSecretPass/12345.ts")]
    [InlineData("https://cdn.example/v/a.m3u8?X-Amz-Security-Token=TOKN007")]
    [InlineData("https://host.example/p?a=1&amp;token=TOPSECRET")]
    [InlineData("https://sso.example/token?client_secret=CS007")]
    [InlineData("https://sso.example/token?refresh_token=RT0042")]
    [InlineData("https://api.example/v1/list?api_key=AKIA9KEY")]
    public void HasCredentials_TrueForTheSameShapes(string url) =>
        Assert.True(CatalogUrlIdentity.HasCredentials(url));

    // SP-0123's promise must survive the widening: a URL with nothing to hide still comes out
    // byte-identical, and '/live/' alone - two segments, no account pair - is not the IPTV shape.
    [Theory]
    [InlineData("PLAY | url=https://host.example/live/news/stream.m3u8 | ok")]
    [InlineData("url=https://host.example/live")]
    [InlineData("url=http://host.example:8080/p?mail=a@b.com")]
    public void RedactText_LeavesCleanShapesAlone(string line)
    {
        Assert.Equal(line, CatalogUrlIdentity.RedactText(line));
    }
}
