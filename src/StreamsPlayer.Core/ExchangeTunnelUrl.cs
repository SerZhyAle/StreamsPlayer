using System.Diagnostics.CodeAnalysis;

namespace StreamsPlayer.Core;

/// <summary>
/// Parser and representation for DEVICE-EXCHANGE tunnel endpoint URLs:
/// <c>fmsx://&lt;publicEndpoint&gt;/b/&lt;broadcastId&gt;/&lt;scheme&gt;</c> (SP-0204 requirement 1).
/// </summary>
public sealed record ExchangeTunnelUrl(string Host, int Port, string BroadcastId, string Scheme)
{
    public const int DefaultExchangePort = 44022;

    public string PublicEndpoint => $"{Host}:{Port}";

    /// <summary>
    /// Parses an <c>fmsx://</c> tunnel endpoint URL.
    /// </summary>
    public static bool TryParse(string? rawUrl, [NotNullWhen(true)] out ExchangeTunnelUrl? tunnelUrl)
    {
        tunnelUrl = null;
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("fmsx", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var port = uri.Port > 0 ? uri.Port : DefaultExchangePort;
        var path = uri.AbsolutePath.Trim('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !parts[0].Equals("b", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        var scheme = parts[2].ToLowerInvariant();
        if (scheme is not ("http" or "rtsp"))
        {
            return false;
        }

        tunnelUrl = new ExchangeTunnelUrl(uri.Host, port, parts[1], scheme);
        return true;
    }

    /// <summary>
    /// Rewrites the host and port of the producer's <paramref name="innerUrl"/> with the loopback forwarder's
    /// address (<c>127.0.0.1:&lt;loopbackPort&gt;</c>), preserving scheme, path and query (SP-0204 requirement 2).
    /// </summary>
    public static string RewriteInner(string innerUrl, int loopbackPort)
    {
        if (!Uri.TryCreate(innerUrl?.Trim(), UriKind.Absolute, out var innerUri) ||
            !LaunchableAddress.IsLaunchable(innerUrl))
        {
            throw new ArgumentException("Inner URL must be a launchable address.", nameof(innerUrl));
        }

        var builder = new UriBuilder(innerUri)
        {
            Host = "127.0.0.1",
            Port = loopbackPort
        };
        return builder.Uri.AbsoluteUri;
    }
}
