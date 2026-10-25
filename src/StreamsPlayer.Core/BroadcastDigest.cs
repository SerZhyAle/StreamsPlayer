using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>What a listener's <c>Authorization: Digest</c> header says, after the checks that need no secret.</summary>
internal sealed record DigestAnswer(
    string User, string Algorithm, string Nonce, string Uri, string NcText, long Nc, string Cnonce, string Response);

/// <summary>
/// The arithmetic and the wire syntax of the producer's HTTP Digest (RFC 7616, <c>qop=auth</c> only) for a protected
/// broadcast (LIVE-BROADCAST item M). Pure functions: the state of a challenge lives in
/// <see cref="BroadcastDigestAuthenticator"/>.
/// </summary>
internal static class BroadcastDigest
{
    internal const string Realm = "fms-broadcast";
    internal const string Sha256 = "SHA-256";
    internal const string Md5 = "MD5";

    // A head is 8 KiB in total; a Digest answer is a few hundred bytes. A longer field is not one this server issued.
    private const int MaximumHeaderLength = 4096;
    private const int MaximumParameters = 16;
    private const int MaximumNonceLength = 64;
    private const int MaximumCnonceLength = 128;
    private const int MaximumUriLength = 2048;

    internal static string Hex(string algorithm, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = algorithm == Sha256 ? SHA256.HashData(bytes) : MD5.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>H(user:realm:password) in lower-case hex - the one value kept per algorithm instead of the password.</summary>
    internal static string Ha1(string algorithm, string user, string realm, string password) =>
        Hex(algorithm, user + ":" + realm + ":" + password);

    /// <summary>The <c>response</c> of RFC 7616 section 3.4.1 for <c>qop=auth</c>.</summary>
    internal static string Response(
        string algorithm, string ha1, string method, string uri, string nonce, string nc, string cnonce)
    {
        var ha2 = Hex(algorithm, method + ":" + uri);
        return Hex(algorithm, ha1 + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + ha2);
    }

    /// <summary>
    /// Reads a Digest answer, or null for anything this server would not have asked for: another scheme, a repeated or
    /// unknown-shaped field, a realm or algorithm it did not offer, a missing or other <c>qop</c>, a malformed <c>nc</c>
    /// or <c>response</c>, or a user-hash or extended-user answer it never invited.
    /// </summary>
    internal static DigestAnswer? ParseAnswer(string? header)
    {
        if (header is null || header.Length > MaximumHeaderLength)
        {
            return null;
        }

        var fields = ParseFields(header.Trim());
        if (fields is null
            || !Field(fields, "username", out var user) || user.Length == 0 || user.Length > 256
            || !Field(fields, "realm", out var realm) || !string.Equals(realm, Realm, StringComparison.Ordinal)
            || !Field(fields, "nonce", out var nonce) || nonce.Length == 0 || nonce.Length > MaximumNonceLength
            || !Field(fields, "uri", out var uri) || uri.Length == 0 || uri.Length > MaximumUriLength
            || !Field(fields, "cnonce", out var cnonce) || cnonce.Length == 0 || cnonce.Length > MaximumCnonceLength
            || !Field(fields, "qop", out var qop) || !qop.Equals("auth", StringComparison.OrdinalIgnoreCase)
            || !Field(fields, "nc", out var ncText) || !TryParseCount(ncText, out var nc)
            || !Field(fields, "response", out var response)
            || fields.ContainsKey("username*") || fields.ContainsKey("userhash"))
        {
            return null;
        }

        // RFC 2617 clients leave the field out when they mean MD5.
        var algorithm = fields.TryGetValue("algorithm", out var named) ? named : Md5;
        if (algorithm.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
        {
            algorithm = Sha256;
        }
        else if (algorithm.Equals(Md5, StringComparison.OrdinalIgnoreCase))
        {
            algorithm = Md5;
        }
        else
        {
            return null;
        }

        var digestLength = algorithm == Sha256 ? 64 : 32;
        if (response.Length != digestLength || !response.All(Uri.IsHexDigit))
        {
            return null;
        }

        return new DigestAnswer(user, algorithm, nonce, uri, ncText, nc, cnonce, response.ToLowerInvariant());
    }

    private static bool Field(Dictionary<string, string> fields, string name, out string value) =>
        fields.TryGetValue(name, out value!);

    private static bool TryParseCount(string text, out long count)
    {
        count = 0;
        return text.Length == 8
            && text.All(Uri.IsHexDigit)
            && long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out count)
            && count >= 1;
    }

    /// <summary>
    /// <c>Digest name=value, name="quoted \" value", ..</c> as a case-insensitive map, or null when the scheme is not
    /// Digest, the syntax is broken, or a name repeats (two readings of one answer is how a check gets bypassed).
    /// </summary>
    private static Dictionary<string, string>? ParseFields(string header)
    {
        const string scheme = "Digest";
        if (header.Length <= scheme.Length
            || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(header[scheme.Length]))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var position = scheme.Length;
        while (true)
        {
            while (position < header.Length && (char.IsWhiteSpace(header[position]) || header[position] == ','))
            {
                position++;
            }

            if (position >= header.Length)
            {
                return fields.Count == 0 ? null : fields;
            }

            var nameStart = position;
            while (position < header.Length && (char.IsAsciiLetterOrDigit(header[position]) || header[position] is '-' or '_' or '*'))
            {
                position++;
            }

            if (position == nameStart)
            {
                return null;
            }

            var name = header[nameStart..position];
            SkipWhitespace(header, ref position);
            if (position >= header.Length || header[position] != '=')
            {
                return null;
            }

            position++;
            SkipWhitespace(header, ref position);
            if (!TryReadValue(header, ref position, out var value) || fields.Count >= MaximumParameters || !fields.TryAdd(name, value))
            {
                return null;
            }

            SkipWhitespace(header, ref position);
            if (position < header.Length && header[position] != ',')
            {
                return null;
            }
        }
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static bool TryReadValue(string text, ref int position, out string value)
    {
        value = "";
        if (position >= text.Length)
        {
            return false;
        }

        if (text[position] != '"')
        {
            var start = position;
            while (position < text.Length && text[position] != ',' && !char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            value = text[start..position];
            return value.Length > 0;
        }

        var builder = new StringBuilder();
        position++;
        while (position < text.Length)
        {
            var current = text[position++];
            if (current == '"')
            {
                value = builder.ToString();
                return true;
            }

            if (current == '\\')
            {
                if (position >= text.Length)
                {
                    return false;
                }

                current = text[position++];
            }

            builder.Append(current);
        }

        return false;
    }
}
