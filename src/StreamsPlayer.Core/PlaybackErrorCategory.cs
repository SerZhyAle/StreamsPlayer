namespace StreamsPlayer.Core;

/// <summary>Stable, coarse classification of a playback failure for a shareable report.</summary>
public enum PlaybackErrorCategory
{
    /// <summary>The media backend refused to start playback.</summary>
    Rejected,

    /// <summary>The backend opened the stream but raised a decode/demux error.</summary>
    MediaError,

    /// <summary>A network-level failure (timeout, connection, DNS, HTTP transport).</summary>
    Network,

    /// <summary>The container/codec/format is not supported by the backend.</summary>
    Unsupported,

    /// <summary>No stable mapping for the reported reason.</summary>
    Unknown
}

/// <summary>
/// Maps the raw failure reasons produced by the players (VLC reason tokens, audio exception type names)
/// to a stable <see cref="PlaybackErrorCategory"/>. Deterministic and total.
/// </summary>
public static class PlaybackErrorClassifier
{
    public static PlaybackErrorCategory Classify(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return PlaybackErrorCategory.Unknown;
        }

        var value = reason.Trim();

        // Player reason tokens.
        if (value.Equals("play_rejected", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackErrorCategory.Rejected;
        }

        if (value.Equals("encountered_error", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackErrorCategory.MediaError;
        }

        // Audio (WPF MediaElement) exception type names and free-text hints.
        if (Contains(value, "format") || Contains(value, "codec") || Contains(value, "notsupported") || Contains(value, "unsupported"))
        {
            return PlaybackErrorCategory.Unsupported;
        }

        if (Contains(value, "network") || Contains(value, "connection") || Contains(value, "timeout") ||
            Contains(value, "socket") || Contains(value, "http") || Contains(value, "webexception") || Contains(value, "dns"))
        {
            return PlaybackErrorCategory.Network;
        }

        if (Contains(value, "com") || Contains(value, "media") || Contains(value, "invalidoperation"))
        {
            return PlaybackErrorCategory.MediaError;
        }

        return PlaybackErrorCategory.Unknown;
    }

    private static bool Contains(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
