using System.Text.RegularExpressions;

namespace StreamsPlayer.Core;

/// <summary>
/// Deterministic stream-URL identity used to decide whether a catalog channel is hidden, and to redact
/// credentials before a URL appears in a shareable failure report or in the diagnostic log. Matching is
/// applied to both the stored hidden identity and the live channel URL, so a catalog refresh that re-adds
/// the exact URL still matches.
/// </summary>
public static partial class CatalogUrlIdentity
{
    /// <summary>What replaces a credential in free text (<c>DIAGNOSTIC-REPORT</c> rule 3).</summary>
    public const string RedactedMarker = "[REDACTED]";

    private static readonly string[] CredentialQueryKeys =
        ["token", "auth", "authorization", "password", "pass", "pwd", "key", "secret", "sig", "signature", "apikey", "access_token"];

    /// <summary>
    /// Idempotent identity: trim, and for an absolute URI lower-case scheme and host while preserving
    /// port, path, query, and fragment. Non-absolute or unparsable input is returned trimmed, unchanged.
    /// </summary>
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
    /// query values are masked. Never emits local filesystem paths. Unparsable input is returned trimmed.
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
            return trimmed;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        var query = RedactQuery(uri.Query);
        return $"{scheme}://{authority}{uri.AbsolutePath}{query}{uri.Fragment}";
    }

    /// <summary>
    /// True when a URL carries credentials in clear text: userinfo (<c>user:pass@host</c>) or a
    /// credential-bearing query value (<see cref="CredentialQueryKeys"/>). Used to warn before an export
    /// writes the URL verbatim. Unparsable input is treated as credential-free.
    /// </summary>
    public static bool HasCredentials(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return true;
        }

        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return false;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator >= 0 ? pair[..separator] : pair;
            if (CredentialQueryKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
    /// userinfo part of the authority is dropped and the value of every credential-bearing query key is
    /// replaced with <see cref="RedactedMarker"/>; scheme, host, port, path and the other query values stay.
    /// </para>
    /// <para>
    /// A URL ends at whitespace, a quote, an angle bracket or <c>|</c> - the log's field separator. An
    /// authority with a non-numeric port and no <c>@</c> is what <c>scheme://user:pass@host</c> looks like
    /// when a cut lands inside it, so such an authority is redacted whole rather than trusted.
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
            authority = RedactedMarker;
        }

        var rest = token[authorityEnd..];
        var queryStart = rest.IndexOf('?');
        if (queryStart >= 0)
        {
            rest = rest[..queryStart] + QueryPair().Replace(
                rest[queryStart..],
                pair => CredentialQueryKeys.Contains(pair.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
                    ? pair.Groups["lead"].Value + RedactedMarker
                    : pair.Value);
        }

        return token[..authorityStart] + authority + rest;
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

    [GeneratedRegex(@"(?<![A-Za-z0-9+.\-])[A-Za-z][A-Za-z0-9+.\-]*://[^\s|""'<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlInText();

    [GeneratedRegex(@"(?<lead>[?&](?<name>[^=&#]*)=)[^&#]*", RegexOptions.CultureInvariant)]
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
            if (CredentialQueryKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                pairs[i] = $"{name}=***";
            }
        }

        return "?" + string.Join('&', pairs);
    }
}
