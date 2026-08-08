using System.IO;

namespace StreamsPlayer.App;

/// <summary>
/// The per-user data directory - catalog state, favicon atlases, grid previews, session logs, and the
/// optional FFmpeg components. It is resolved in several unrelated places (startup logging, the main
/// window's store, the video backend), so it is declared once here rather than re-composed each time.
/// </summary>
internal static class AppPaths
{
    /// <summary><c>%LOCALAPPDATA%\StreamsPlayer</c>.</summary>
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamsPlayer");
}
