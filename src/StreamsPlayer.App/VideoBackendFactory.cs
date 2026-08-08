using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026 - picks the video/RTSP playback engine from the persisted <see cref="MediaBackend"/>.
/// LibVLC is the default and the fallback for every value; FlyleafLib is the opt-in alternative.
/// </summary>
internal static class VideoBackendFactory
{
    public static IVideoBackend Create(MediaBackend backend, int volume, bool muted, CurrentLog log)
    {
        if (backend == MediaBackend.Flyleaf)
        {
            try
            {
                return new FlyleafVideoBackend(volume, muted, log);
            }
            catch (Exception ex)
            {
                // FlyleafLib could not initialize (missing FFmpeg components, win-arm64, or an engine fault).
                // Fall back to the proven LibVLC engine rather than failing playback (experimental, not a crash).
                // Settings warns the user up front when the components are the reason, so this is a log
                // record for the case they were present and the engine still refused.
                var folder = FlyleafVideoBackend.ResolveFFmpegPath(out _);
                var missing = FFmpegComponents.MissingLibraries(folder);
                log.Event(
                    "FLYLEAF FALLBACK",
                    "to=libvlc",
                    $"reason={ex.GetType().Name}",
                    $"components={(missing.Count == 0 ? "present" : $"missing:{string.Join('+', missing)}")}",
                    $"err={ex.Message}");
            }
        }

        return new LibVlcVideoBackend(volume, muted, log);
    }
}
