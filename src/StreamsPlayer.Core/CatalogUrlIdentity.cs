using System.Text.RegularExpressions;

namespace StreamsPlayer.Core;

/// <summary>
/// Deterministic stream-URL identity used to decide whether a catalog channel is hidden, and to redact
/// credentials before a URL appears in a shareable failure report or in the diagnostic log. Matching is
/// applied to both the stored hidden identity and the live channel URL, so a catalog refresh that re-adds
/// the exact URL still matches.
/// </summary>
/// <remarks>
/// <para>
/// SP-0174: the one rule for what an outward-facing address may contain lives here and is shared by
/// <see cref="Redact"/>, <see cref="RedactText"/> and <see cref="HasCredentials"/> (and, through the last,
/// by later consumers such as launch arguments). It covers userinfo with any character in its password, the
/// documented list of secret query names in <see cref="CredentialQueryKeys"/> matched case-insensitively
/// and in HTML-escaped form (<c>&amp;amp;token=</c>), and the credential-in-path shapes of
/// <see cref="RedactCredentialPathSegments"/>.
/// </para>
/// </remarks>
public static partial class CatalogUrlIdentity
{
    /// <summary>What replaces a credential in free text (<c>DIAGNOSTIC-REPORT</c> rule 3).</summary>
    public const string RedactedMarker = "[REDACTED]";

    /// <summary>
    /// The documented secret query names (<c>DIAGNOSTIC-REPORT</c> rule 3): the twelve SP-0123 names plus
    /// the SP-0174 additions - API-key spellings, OAuth refresh and client secrets, the signed-CDN
    /// parameters of S3 and CloudFront, and the three signing schemes seen on IPTV edge servers.
    /// Matched case-insensitively, in plain and HTML-escaped form.
    /// </summary>
    private static readonly string[] CredentialQueryKeys =
    [
        "token", "auth", "authorization", "password", "pass", "pwd", "key", "secret", "sig", "signature", "apikey", "access_token",
        "api_key", "client_secret", "refresh_token",
        "x-amz-signature", "x-amz-credential", "x-amz-security-token",
        "wmsauthsign", "hdnts", "hdnea",
        "policy", "key-pair-id",
    ];

    /// <summary>An HTML-escaped pair separator reads <c>&amp;amp;name=</c> in a log; the prefix is stripped before matching.</summary>
    private const string EscapedPairPrefix = "amp;";

    /// <summary>
    /// Idempotent identity: trim, and for an absolute URI lower-case scheme and host while preserving
    /// port, path, query, and fragment. Non-absolute or unparsable input is returned trimmed, unchanged.
    /// </summary>
    /// <remarks>
    /// Userinfo (<c>user:pass@</c>) is deliberately <b>not</b> part of the identity: it is rebuilt from
    /// scheme, host and port only, so <c>http://u:p@host/x</c> and <c>http://host/x</c> are the same stream
    /// (the same address with and without its login is one channel for hiding and de-duplication), and an
    /// identity string never carries a password into a file or a log (SP-0184, S12-3).
    /// </remarks>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        return $"{scheme}://{authority}{uri.PathAndQuery}{uri.Fragment}";
    }

    /// <summary>True when two URLs resolve to the same normalized identity.</summary>
    public static bool SameIdentity(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    /// <summary>True when <paramref name="channelUrl"/> matches any hidden identity.</summary>
    public static bool IsHidden(IEnumerable<string> hiddenUrls, string channelUrl)
    {
        var target = Normalize(channelUrl);
        foreach (var hidden in hiddenUrls)
        {
            if (string.Equals(Normalize(hidden), target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a display-safe URL for reports: userinfo (user:pass@) is dropped and credential-bearing
    /// query values are masked. Never emits local filesystem paths. Unparsable input goes through
    /// <see cref="RedactText"/> - SP-0174: a password containing <c>/</c>, <c>?</c> or <c>#</c> makes the
    /// whole address unparsable, and returning it verbatim would return the secret.
    /// </summary>
    public static string Redact(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return RedactText(trimmed);
        }

        // SP-0184 (S12-2): a digits-only password with a "/" parses as host:port plus a path, so the
        // secret sits in plain sight; the text redactor knows the shape, hand it over whole.
        var authorityFrom = trimmed.IndexOf("://", StringComparison.Ordinal) + 3;
        if (authorityFrom >= 3)
        {
            var authorityTo = trimmed.IndexOfAny(['/', '?', '#'], authorityFrom);
            if (authorityTo >= 0 && TryFindNumericPasswordSplit(trimmed, authorityFrom, authorityTo, out _))
            {
                return RedactText(trimmed);
            }
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        var query = RedactQuery(uri.Query);
        return $"{scheme}://{authority}{RedactCredentialPathSegments(uri.AbsolutePath)}{query}{uri.Fragment}";
    }

    /// <summary>
    /// True when the log redactor would remove anything from this address. Used by exports and launch
    /// arguments so both follow the same credential rule as logs and diagnostic archives.
    /// </summary>
    public static bool HasCredentials(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var trimmed = url.Trim();
        return !string.Equals(trimmed, RedactText(trimmed), StringComparison.Ordinal);
    }

    /// <summary>
    /// Redacts every URL found inside free text - a log line, an exception message, a whole log file -
    /// and leaves every other character exactly as it was (SP-0123, <c>DIAGNOSTIC-REPORT</c> rule 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Redact"/> this never parses and never re-renders a URL: the diagnostic log keeps
    /// full addresses for measurement (SP-0040), so a URL without credentials must come out byte-identical,
    /// and a URL that a compacted or truncated log cut in half must still lose its secret. Per URL, the
    /// userinfo part of the authority is dropped and the value of every secret query key is replaced with
    /// <see cref="RedactedMarker"/>; scheme, host, port, path and the other query values stay.
    /// </para>
    /// <para>
    /// A URL ends at whitespace, a quote, an angle bracket or <c>|</c> - the log's field separator. An
    /// authority with a non-numeric port and no <c>@</c> is what <c>scheme://user:pass@host</c> looks like
    /// when a cut landed inside it, so such an authority is redacted whole rather than trusted - unless a
    /// plausible host follows a later <c>@</c>, which is what the same URL looks like when the password
    /// itself contains <c>/</c>, <c>?</c> or <c>#</c> (SP-0174): then everything through that <c>@</c> is
    /// userinfo and is dropped, host included in what survives.
    /// </para>
    /// </remarks>
    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("://", StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        return UrlInText().Replace(text, match => RedactUrlToken(match.Value));
    }

    private static string RedactUrlToken(string token)
    {
        var authorityStart = token.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = token.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = token.Length;
        }

        var authority = token[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }
        else if (LooksLikeCutUserInfo(authority))
        {
            var hostAt = IndexOfHostAt(token, authorityEnd);
            if (hostAt < 0)
            {
                authority = RedactedMarker;
            }
            else
            {
                (authority, authorityEnd) = HostAfterUserInfo(token, hostAt);
            }
        }
        else if (TryFindNumericPasswordSplit(token, authorityStart, authorityEnd, out var passwordHostAt))
        {
            (authority, authorityEnd) = HostAfterUserInfo(token, passwordHostAt);
        }

        var rest = token[authorityEnd..];
        var queryStart = rest.IndexOf('?');
        var fragmentStart = rest.IndexOf('#');
        var pathEnd = queryStart >= 0
            ? (fragmentStart >= 0 ? Math.Min(queryStart, fragmentStart) : queryStart)
            : (fragmentStart >= 0 ? fragmentStart : rest.Length);
        var path = RedactCredentialPathSegments(rest[..pathEnd]);
        var tail = rest[pathEnd..];
        if (queryStart >= 0)
        {
            tail = QueryPair().Replace(
                tail,
                pair => IsSecretQueryName(pair.Groups["name"].Value)
                    ? pair.Groups["lead"].Value + RedactedMarker
                    : pair.Value);
        }

        return token[..authorityStart] + authority + path + tail;
    }

    /// <summary>The host that follows the userinfo-closing <c>@</c> at <paramref name="at"/>, and where it ends.</summary>
    private static (string Host, int End) HostAfterUserInfo(string token, int at)
    {
        var hostEnd = token.IndexOfAny(['/', '?', '#'], at + 1);
        if (hostEnd < 0)
        {
            hostEnd = token.Length;
        }

        return (token[(at + 1)..hostEnd], hostEnd);
    }

    /// <summary>
    /// SP-0184 (S12-2): a password made only of digits and containing <c>/</c> (<c>user:1234/ab@host/x</c>)
    /// leaves an authority, <c>user:1234</c>, that is indistinguishable from a legitimate <c>host:port</c>,
    /// so <see cref="LooksLikeCutUserInfo"/> lets it through and the secret survives. The way to tell the two
    /// apart is what follows: a later host-shaped <c>@</c> with no query punctuation between it and the
    /// authority. A real <c>host:8080/path?mail=a@b.example</c> has <c>?</c> or <c>=</c> in that gap and is
    /// left alone; a plain-path <c>host:8080/a@b.example</c> is the one shape the heuristic still redacts
    /// wrongly, which is the right side to err on for a credential rule.
    /// </summary>
    private static bool TryFindNumericPasswordSplit(string text, int authorityStart, int authorityEnd, out int hostAt)
    {
        hostAt = -1;
        var authority = text[authorityStart..authorityEnd];
        if (authority.StartsWith('['))
        {
            return false;
        }

        var colon = authority.LastIndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var port = authority[(colon + 1)..];
        if (port.Length == 0 || !port.All(char.IsAsciiDigit))
        {
            return false;
        }

        var candidate = IndexOfHostAt(text, authorityEnd);
        if (candidate < 0 || text[authorityEnd..candidate].IndexOfAny(['?', '#', '&', '=']) >= 0)
        {
            return false;
        }

        hostAt = candidate;
        return true;
    }

    /// <summary>
    /// The first <c>@</c> after <paramref name="from"/> that is followed by something that can be a host.
    /// A bare first <c>@</c> would misread the query of a malformed address (<c>?mail=a@b</c>) as the
    /// userinfo separator and destroy the URL, so candidates without a host shape are skipped.
    /// </summary>
    private static int IndexOfHostAt(string token, int from)
    {
        for (var at = token.IndexOf('@', from); at >= 0; at = token.IndexOf('@', at + 1))
        {
            var end = token.IndexOfAny(['/', '?', '#'], at + 1);
            if (end < 0)
            {
                end = token.Length;
            }

            if (LooksLikeHost(token[(at + 1)..end]))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>An IPv6 literal in brackets, or a name whose only colon ends a non-empty numeric port.</summary>
    private static bool LooksLikeHost(string candidate)
    {
        if (candidate.Length == 0)
        {
            return false;
        }

        if (candidate.StartsWith('['))
        {
            return candidate.EndsWith(']');
        }

        var colon = candidate.LastIndexOf(':');
        if (colon >= 0)
        {
            var port = candidate[(colon + 1)..];
            if (port.Length == 0 || !port.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return candidate.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '[' or ']');
    }

    private static bool LooksLikeCutUserInfo(string authority)
    {
        if (authority.StartsWith('['))
        {
            return false; // An IPv6 literal: its colons are the address, not a password.
        }

        var colon = authority.LastIndexOf(':');
        return colon >= 0 && !authority[(colon + 1)..].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The documented credential-in-path shapes (SP-0174): the Xtream-style IPTV address
    /// <c>/<live|movie|series>/&lt;user&gt;/&lt;pass&gt;/&lt;id&gt;</c> carries the account in the path, and
    /// an imported playlist hands the player exactly this. Only the two account segments are replaced; the
    /// kind and the content id stay, because they are the diagnosis.
    /// </summary>
    [GeneratedRegex(@"^/(?<kind>live|movie|series)/(?<user>[^/]+)/(?<pass>[^/]+)/(?<rest>.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPathShape();

    private static string RedactCredentialPathSegments(string path)
    {
        var shape = CredentialPathShape().Match(path);
        return shape.Success
            ? $"/{shape.Groups["kind"].Value}/{RedactedMarker}/{RedactedMarker}/{shape.Groups["rest"].Value}"
            : path;
    }

    private static bool IsSecretQueryName(string name) =>
        CredentialQueryKeys.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        (name.StartsWith(EscapedPairPrefix, StringComparison.Ordinal) &&
         CredentialQueryKeys.Contains(name[EscapedPairPrefix.Length..], StringComparer.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<![A-Za-z0-9+.\-])[A-Za-z][A-Za-z0-9+.\-]*://[^\s|""'<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlInText();

    [GeneratedRegex(@"(?<lead>[?&](?:amp;)?(?<name>[^=&#]*)=)[^&#]*", RegexOptions.CultureInvariant)]
    private static partial Regex QueryPair();

    private static string RedactQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        var pairs = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < pairs.Length; i++)
        {
            var separator = pairs[i].IndexOf('=');
            var name = separator >= 0 ? pairs[i][..separator] : pairs[i];
            if (IsSecretQueryName(name))
            {
                pairs[i] = $"{name}=***";
            }
        }

        return "?" + string.Join('&', pairs);
    }
}
