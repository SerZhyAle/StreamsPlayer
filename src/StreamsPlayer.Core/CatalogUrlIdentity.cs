namespace StreamsPlayer.Core;

/// <summary>
/// Deterministic stream-URL identity used to decide whether a catalog channel is hidden, and to redact
/// credentials before a URL appears in a shareable failure report. Matching is applied to both the stored
/// hidden identity and the live channel URL, so a catalog refresh that re-adds the exact URL still matches.
/// </summary>
public static class CatalogUrlIdentity
{
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
