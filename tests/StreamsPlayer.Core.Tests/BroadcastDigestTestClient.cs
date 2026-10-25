using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// A Digest client written from RFC 7616 alone, with nothing but System.Security.Cryptography: the independent side of
/// the producer's check. It shares no code with the server, so an answer it builds that the server accepts is evidence
/// of the format, not of the server agreeing with itself.
/// </summary>
internal static partial class BroadcastDigestTestClient
{
    public const string Sha256 = "SHA-256";
    public const string Md5 = "MD5";

    [GeneratedRegex("""([A-Za-z0-9_-]+)\s*=\s*(?:"([^"]*)"|([^\s,]+))""")]
    private static partial Regex Field();

    /// <summary>The fields of one challenge line (<c>Digest realm="..", ..</c>), names lower-cased.</summary>
    public static Dictionary<string, string> ParseChallenge(string line)
    {
        Assert.StartsWith("Digest ", line, StringComparison.Ordinal);
        return Field().Matches(line["Digest ".Length..])
            .ToDictionary(match => match.Groups[1].Value.ToLowerInvariant(),
                match => match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
    }

    public static string Hex(string algorithm, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(algorithm == Sha256 ? SHA256.HashData(bytes) : MD5.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>The <c>response</c> of RFC 7616 section 3.4.1 with <c>qop=auth</c>.</summary>
    public static string Response(
        string algorithm, string user, string realm, string password, string method, string uri, string nonce, string nc, string cnonce)
    {
        var ha1 = Hex(algorithm, user + ":" + realm + ":" + password);
        var ha2 = Hex(algorithm, method + ":" + uri);
        return Hex(algorithm, ha1 + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + ha2);
    }

    /// <summary>Answers the challenge line of <paramref name="algorithm"/> the way a conforming client does.</summary>
    public static string Authorize(
        IReadOnlyList<string> challenge, string algorithm, string user, string password, string method, string uri,
        int nc = 1, string cnonce = "0a4f113b5c", string? algorithmToDeclare = null, string? uriToHash = null)
    {
        var line = challenge.Single(candidate => ParseChallenge(candidate)["algorithm"] == algorithm);
        var fields = ParseChallenge(line);
        var ncText = nc.ToString("x8");
        var response = Response(algorithm, user, fields["realm"], password, method, uriToHash ?? uri, fields["nonce"], ncText, cnonce);
        return $"Digest username=\"{user}\", realm=\"{fields["realm"]}\", nonce=\"{fields["nonce"]}\", uri=\"{uri}\", "
            + $"algorithm={algorithmToDeclare ?? algorithm}, nc={ncText}, cnonce=\"{cnonce}\", qop=auth, response=\"{response}\"";
    }
}

/// <summary>A clock a test moves by hand, for the nonce lifetime and the damper's quiet period.</summary>
internal sealed class BroadcastTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}
