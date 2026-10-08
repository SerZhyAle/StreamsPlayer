namespace StreamsPlayer.Core;

public static class StreamMediaKindClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u8", ".mpd", ".mp4", ".mkv", ".webm", ".ts", ".mov"
    };

    public static bool IsLaunchable(string? value) => LaunchableAddress.IsLaunchable(value);

    public static MediaKind Classify(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return MediaKind.Audio;
        }

        if (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Rtsp;
        }

        if (uri.Scheme.Equals("fmsx", StringComparison.OrdinalIgnoreCase) &&
            ExchangeTunnelUrl.TryParse(url, out var tunnelUrl))
        {
            return tunnelUrl.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
                ? MediaKind.Rtsp
                : MediaKind.Audio;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        return VideoExtensions.Contains(extension) ? MediaKind.Video : MediaKind.Audio;
    }

    public static MediaKind FromCatalogValue(string? declaredKind, string url) =>
        declaredKind?.Trim().ToUpperInvariant() switch
        {
            "AUDIO" => MediaKind.Audio,
            "VIDEO" => MediaKind.Video,
            "RTSP" => MediaKind.Rtsp,
            _ => Classify(url)
        };
}
