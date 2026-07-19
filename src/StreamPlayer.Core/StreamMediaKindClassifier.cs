namespace StreamPlayer.Core;

public static class StreamMediaKindClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u8", ".mpd", ".mp4", ".mkv", ".webm", ".ts", ".mov"
    };

    public static bool IsLaunchable(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase));

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
