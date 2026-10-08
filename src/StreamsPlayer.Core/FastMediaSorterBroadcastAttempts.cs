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
        string.Equals(endpoint.Transport, "RELAY", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsDeclaredRtspEndpoint(string descriptorMode, FastMediaSorterBroadcastEndpoint endpoint) =>
        string.Equals(endpoint.Mode, descriptorMode, StringComparison.Ordinal) &&
        string.Equals(endpoint.Transport, "RTSP", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTunnelAudio(FastMediaSorterBroadcastEndpoint endpoint) =>
        (endpoint.Mode is null || string.Equals(endpoint.Mode, FastMediaSorterBroadcastDescriptor.AudioOnlyMode, StringComparison.Ordinal)) &&
        string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase) &&
        ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnel) &&
        tunnel.Scheme == "http" &&
        endpoint.Inner is not null &&
        LaunchableAddress.TryParseHttp(endpoint.Inner, out _);

    private static bool IsTunnelVideo(string descriptorMode, FastMediaSorterBroadcastEndpoint endpoint) =>
        (endpoint.Mode is null || string.Equals(endpoint.Mode, descriptorMode, StringComparison.Ordinal)) &&
        string.Equals(endpoint.Transport, "TUNNEL", StringComparison.OrdinalIgnoreCase) &&
        ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnel) &&
        (tunnel.Scheme is "rtsp" or "http") &&
        endpoint.Inner is not null &&
        LaunchableAddress.TryParse(endpoint.Inner, out _);
}
