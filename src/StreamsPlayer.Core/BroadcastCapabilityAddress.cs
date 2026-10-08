namespace StreamsPlayer.Core;

/// <summary>
/// SP-0201 requirement 5 - a relay or tunnel address contains the broadcast id, which is the capability to
/// listen. This is the one test an export or a share runs before handing an address out: the user must be
/// told that sharing the address hands out the listening right.
/// </summary>
public static class BroadcastCapabilityAddress
{
    /// <summary>
    /// Whether <paramref name="url"/> is an exchange endpoint address - a tunnel (<c>fmsx:</c>) or a relay
    /// listen path (<c>https://../v2/b/&lt;broadcastId&gt;/stream</c>). A LAN address carries no id and is
    /// never one of these.
    /// </summary>
    public static bool CarriesCapability(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals("fmsx", StringComparison.OrdinalIgnoreCase)
            || (uri.Scheme == Uri.UriSchemeHttps
                && uri.AbsolutePath.StartsWith("/v2/b/", StringComparison.OrdinalIgnoreCase));
    }
}
