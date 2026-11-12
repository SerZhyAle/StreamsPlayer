using System.Globalization;
using System.Security.Cryptography;

namespace StreamsPlayer.Core;

/// <summary>One connection's Digest challenge. Credentials never leave this object as plain text.</summary>
internal sealed class BroadcastDigestClient
{
    private readonly Dictionary<string, string> _fields;
    private readonly string _algorithm;
    private readonly bool _qop;
    private readonly string _cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private uint _count;

    private BroadcastDigestClient(Dictionary<string, string> fields, string algorithm, bool qop)
    {
        _fields = fields;
        _algorithm = algorithm;
        _qop = qop;
    }

    internal bool Stale => _fields.TryGetValue("stale", out var stale) && stale.Equals("true", StringComparison.OrdinalIgnoreCase);

    internal static BroadcastDigestClient? Select(IEnumerable<string> challenges) =>
        challenges.Select(Parse).Where(value => value is not null)
            .OrderByDescending(value => value!._algorithm == BroadcastDigest.Sha256).FirstOrDefault();

    private static BroadcastDigestClient? Parse(string header)
    {
        if (header.Length > 4096 || header.Any(char.IsControl)) return null;
        var fields = BroadcastDigest.ParseFields(header);
        if (fields is null || !fields.TryGetValue("realm", out var realm) || realm.Length > 256
            || !fields.TryGetValue("nonce", out var nonce) || nonce.Length is 0 or > 256
            || fields.ContainsKey("userhash")) return null;
        var algorithm = fields.GetValueOrDefault("algorithm", BroadcastDigest.Md5).ToUpperInvariant();
        if (algorithm is not (BroadcastDigest.Md5 or BroadcastDigest.Sha256)) return null;
        var qop = fields.TryGetValue("qop", out var choices);
        if (qop && !choices!.Split(',').Any(value => value.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase))) return null;
        if (fields.TryGetValue("charset", out var charset) && !charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)) return null;
        return new(fields, algorithm, qop);
    }

    internal string Answer(ExchangeRelayCredentials credentials, string method, string uri)
    {
        if (_count == uint.MaxValue) throw new InvalidOperationException("Digest nonce count exhausted.");
        var nc = (++_count).ToString("x8", CultureInfo.InvariantCulture);
        var realm = _fields["realm"];
        var nonce = _fields["nonce"];
        var ha1 = BroadcastDigest.Ha1(_algorithm, credentials.User, realm, credentials.Password);
        var response = _qop
            ? BroadcastDigest.Response(_algorithm, ha1, method, uri, nonce, nc, _cnonce)
            : BroadcastDigest.Hex(_algorithm, ha1 + ":" + nonce + ":" + BroadcastDigest.Hex(_algorithm, method + ":" + uri));
        var answer = $"Digest username={Quote(credentials.User)}, realm={Quote(realm)}, nonce={Quote(nonce)}, uri={Quote(uri)}, algorithm={_algorithm}, response={Quote(response)}";
        if (_qop) answer += $", qop=auth, nc={nc}, cnonce={Quote(_cnonce)}";
        if (_fields.TryGetValue("opaque", out var opaque)) answer += ", opaque=" + Quote(opaque);
        return answer;
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl)) throw new ArgumentException("Invalid Digest field.");
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
