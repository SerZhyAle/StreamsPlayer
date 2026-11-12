using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastDigestClientTests
{
    [Theory]
    [InlineData("MD5", true)]
    [InlineData("SHA-256", true)]
    [InlineData("MD5", false)]
    public void AnswerMatchesIndependentArithmetic(string algorithm, bool qop)
    {
        var challenge = $"Digest realm=\"test\", nonce=\"abc\", algorithm={algorithm}, opaque=\"xyz\"" + (qop ? ", qop=\"auth,auth-int\"" : "");
        var client = BroadcastDigestClient.Select([challenge])!;
        var header = client.Answer(new("listener", "secret"), "DESCRIBE", "rtsp://phone/live");
        var fields = BroadcastDigest.ParseFields(header)!;
        string Hash(string text) => Convert.ToHexStringLower(algorithm == "MD5"
            ? MD5.HashData(Encoding.UTF8.GetBytes(text)) : SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var a1 = Hash("listener:test:secret");
        var a2 = Hash("DESCRIBE:rtsp://phone/live");
        var expected = qop ? Hash($"{a1}:abc:00000001:{fields["cnonce"]}:auth:{a2}") : Hash($"{a1}:abc:{a2}");
        Assert.Equal(expected, fields["response"]);
        Assert.Equal("xyz", fields["opaque"]);
        Assert.DoesNotContain("secret", header);
        if (qop) Assert.Contains("nc=00000002", client.Answer(new("listener", "secret"), "PLAY", "rtsp://phone/live"));
    }

    [Fact]
    public void PrefersSha256AndNeverDowngradesToBasic()
    {
        var client = BroadcastDigestClient.Select(["Basic realm=\"test\"", "Digest realm=\"test\", nonce=\"a\", algorithm=MD5", "Digest realm=\"test\", nonce=\"b\", algorithm=SHA-256"]);
        Assert.Contains("algorithm=SHA-256", client!.Answer(new("u", "p"), "GET", "/"));
        Assert.Null(BroadcastDigestClient.Select(["Basic realm=\"test\""]));
    }

    [Theory]
    [InlineData("Digest realm=\"test\", nonce=\"a\", qop=\"auth-int\"")]
    [InlineData("Digest realm=\"test\", nonce=\"a\", algorithm=unknown")]
    [InlineData("Digest realm=\"test\", nonce=\"a\", nonce=\"b\"")]
    [InlineData("Digest realm=\"test\", nonce=\"a\r\nb\"")]
    public void RefusesUnsupportedOrMalformedChallenge(string challenge) => Assert.Null(BroadcastDigestClient.Select([challenge]));
}
