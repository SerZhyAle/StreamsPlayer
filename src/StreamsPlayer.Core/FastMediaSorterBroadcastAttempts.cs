namespace StreamsPlayer.Core;

/// <summary>
/// SP-0203: the endpoint-order rules shared by the descriptor (at hand-off/import time) and the
/// persisted broadcast info (every playback leg). The producer's list order is the order a leg
/// tries endpoints in and the order a reconnect restarts at; a transport this build does not
/// implement (`P2P` stays reserved) and an endpoint of another mode drop silently, as
/// LIVE-BROADCAST section 3.2 requires.
/// </summary>
public static class FastMediaSorterBroadcastAttempts
{
    /// <summary>The endpoints this build can play for this mode, in the producer's listed order.</summary>
    public static IReadOnlyList<FastMediaSorterBroadcastEndpoint> PlaybackAttempts(
        string mode, IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints) =>
        endpoints.Where(endpoint => IsPlaybackEndpoint(mode, endpoint)).ToArray();

    /// <summary>The first playable audio attempt, or the legacy top-level endpoint.</summary>
    public static FastMediaSorterBroadcastEndpoint SelectAudio(
        string mode, string legacyUrl, bool isLive, long? targetLatencyMs,
        IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints) =>
        AudioAttempts(mode, endpoints).FirstOrDefault() ?? Legacy(legacyUrl, mode, isLive, targetLatencyMs);

    /// <summary>The first playable video attempt, or the legacy top-level endpoint.</summary>
    public static FastMediaSorterBroadcastEndpoint SelectVideo(
        string mode, string legacyUrl, bool isLive, long? targetLatencyMs,
        IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints) =>
        VideoAttempts(mode, endpoints).FirstOrDefault() ?? Legacy(legacyUrl, mode, isLive, targetLatencyMs);

    /// <summary>The legacy top-level address as the single attempt of a descriptor with no usable list.</summary>
    public static FastMediaSorterBroadcastEndpoint Legacy(string url, string mode, bool isLive, long? targetLatencyMs) =>
        new(
            url,
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Scheme.ToUpperInvariant() : null,
            mode,
            null,
            null,
            null,
            null,
            isLive,
            targetLatencyMs);

    private static IEnumerable<FastMediaSorterBroadcastEndpoint> AudioAttempts(
        string mode, IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints) =>
        endpoints.Where(endpoint =>
            IsHttpAudio(endpoint) ||
            IsRelayEndpoint(endpoint) && IsAudioMode(endpoint.Mode) ||
            IsTunnelAudio(endpoint));

    private static IEnumerable<FastMediaSorterBroadcastEndpoint> VideoAttempts(
        string mode, IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints) =>
        endpoints.Where(endpoint =>
            IsDeclaredRtspEndpoint(mode, endpoint) ||
            IsHttpVideoEndpoint(mode, endpoint) ||
            IsRelayEndpoint(endpoint) && IsVideoMode(mode, endpoint.Mode) ||
            IsTunnelVideo(mode, endpoint));

    private static bool IsPlaybackEndpoint(string mode, FastMediaSorterBroadcastEndpoint endpoint) =>
        FastMediaSorterBroadcastDescriptor.IsVideoMode(mode)
            ? IsDeclaredRtspEndpoint(mode, endpoint) || IsHttpVideoEndpoint(mode, endpoint) ||
              IsRelayEndpoint(endpoint) && IsVideoMode(mode, endpoint.Mode) || IsTunnelVideo(mode, endpoint)
            : IsHttpAudio(endpoint) || IsRelayEndpoint(endpoint) && IsAudioMode(endpoint.Mode) ||
              IsTunnelAudio(endpoint);

    private static bool IsVideoMode(string descriptorMode, string? endpointMode) =>
        endpointMode is null || string.Equals(endpointMode, descriptorMode, StringComparison.Ordinal);

    private static bool IsAudioMode(string? endpointMode) =>
        endpointMode is null ||
        string.Equals(endpointMode, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, StringComparison.Ordinal);

    private static bool IsHttpAudio(FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Mode, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, StringComparison.Ordinal) &&
        string.Equals(endpoint.Transport, "HTTP", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The LAN HTTP video shape of amendment item C - MPEG-TS over `http(s)`, whose URL carries the
    /// producer's path (`/live.ts`, or a token segment before it) and needs no extension check here.
    /// An absent endpoint mode inherits the descriptor's.
    /// </summary>
    private static bool IsHttpVideoEndpoint(string descriptorMode, FastMediaSorterBroadcastEndpoint endpoint) =>
        IsVideoMode(descriptorMode, endpoint.Mode) &&
        string.Equals(endpoint.Transport, "HTTP", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The relay listen of DEVICE-EXCHANGE section 7.6 - always HTTPS; the body is ADTS for the
    /// audio mode and MPEG-TS for a video mode, both a plain GET.
    /// </summary>
    private static bool IsRelayEndpoint(FastMediaSorterBroadcastEndpoint endpoint) =>
        IsRelayShape(endpoint) && IsSealed(endpoint);

    private static bool IsRelayShape(FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Transport, "RELAY", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsDeclaredRtspEndpoint(string descriptorMode, FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Mode, descriptorMode, StringComparison.Ordinal) &&
        string.Equals(endpoint.Transport, "RTSP", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTunnelAudio(FastMediaSorterBroadcastEndpoint endpoint) =>
        IsTunnelAudioShape(endpoint) && IsSealed(endpoint);

    private static bool IsTunnelAudioShape(FastMediaSorterBroadcastEndpoint endpoint) =>
        (endpoint.Mode is null || string.Equals(endpoint.Mode, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, StringComparison.Ordinal)) &&
        string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase) &&
        ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnel) &&
        tunnel.Scheme == "http" &&
        endpoint.Inner is not null &&
        LaunchableAddress.TryParseHttp(endpoint.Inner, out _);

    private static bool IsTunnelVideo(string descriptorMode, FastMediaSorterBroadcastEndpoint endpoint) =>
        IsTunnelVideoShape(descriptorMode, endpoint) && IsSealed(endpoint);

    private static bool IsTunnelVideoShape(string descriptorMode, FastMediaSorterBroadcastEndpoint endpoint) =>
        (endpoint.Mode is null || string.Equals(endpoint.Mode, descriptorMode, StringComparison.Ordinal)) &&
        string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase) &&
        ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnel) &&
        (tunnel.Scheme is "rtsp" or "http") &&
        endpoint.Inner is not null &&
        LaunchableAddress.TryParse(endpoint.Inner, out _);

    /// <summary>
    /// SP-0226 / `DEVICE-EXCHANGE` items DK and DP: an exchange endpoint is played only when it declares a
    /// `seal` version this build knows. An endpoint with none (an older producer) or with another version is not an
    /// attempt - it is never played as clear bytes, and the person is told (<see cref="HasUnsealedExchangeEndpoint"/>).
    /// A LAN endpoint does not pass through a server and is not judged here.
    /// </summary>
    private static bool IsSealed(FastMediaSorterBroadcastEndpoint endpoint) => endpoint.Seal is { IsKnown: true };

    /// <summary>
    /// Whether the descriptor lists a relay or tunnel endpoint of this build's shape that was left out for want of a
    /// known seal - the case the person must be told about instead of seeing nothing play.
    /// </summary>
    public static bool HasUnsealedExchangeEndpoint(string mode, IReadOnlyList<FastMediaSorterBroadcastEndpoint> endpoints) =>
        endpoints.Any(endpoint => !IsSealed(endpoint) &&
            (FastMediaSorterBroadcastDescriptor.IsVideoMode(mode)
                ? IsRelayShape(endpoint) && IsVideoMode(mode, endpoint.Mode) || IsTunnelVideoShape(mode, endpoint)
                : IsRelayShape(endpoint) && IsAudioMode(endpoint.Mode) || IsTunnelAudioShape(endpoint)));
}

/// <summary>
/// Which addresses are the exchange server's own. The test is the address, not the declared transport: a
/// descriptor's top-level `url` is the relay or the tunnel when there is no LAN endpoint, and a sealed
/// broadcast must not be reachable as clear bytes by falling back to it (`DEVICE-EXCHANGE` item DP).
/// </summary>
public static class ExchangeEndpointKinds
{
    /// <summary>`fmsx://host/b/&lt;id&gt;/&lt;scheme&gt;` (a tunnel) or `https://host/v2/b/&lt;id&gt;/stream` (a relay listen).</summary>
    public static bool IsExchangeAddress(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme.Equals("fmsx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = uri.AbsolutePath;
        return uri.Scheme == Uri.UriSchemeHttps && path.StartsWith("/v2/b/", StringComparison.Ordinal)
            && path.EndsWith("/stream", StringComparison.Ordinal);
    }

    /// <summary>An endpoint that passes through an exchange server: declared as `RELAY` or `TUNNEL`, or addressed as one.</summary>
    public static bool IsExchangeEndpoint(FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Transport, "RELAY", StringComparison.OrdinalIgnoreCase)
        || string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase)
        || IsExchangeAddress(endpoint.Url);

    /// <summary>The one rule: an exchange endpoint without a known seal is refused, whatever else describes it.</summary>
    public static bool IsRefusedAsUnsealed(FastMediaSorterBroadcastEndpoint endpoint) =>
        IsExchangeEndpoint(endpoint) && endpoint.Seal is not { IsKnown: true };
}
