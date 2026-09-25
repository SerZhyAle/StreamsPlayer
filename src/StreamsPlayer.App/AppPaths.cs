using System.IO;

namespace StreamsPlayer.App;

/// <summary>
/// The per-user data directory - catalog state, favicon atlases, grid previews, session logs, and the
/// optional FFmpeg components. It is resolved in several unrelated places (startup logging, the main
/// window's store, the video backend), so it is declared once here rather than re-composed each time.
/// </summary>
internal static class AppPaths
{
    /// <summary>
    /// SP-0133: relocates the data directory for one process. Set only by <c>scripts/smoke-playback.ps1</c>, so the
    /// playback gate runs against a throw-away profile instead of the owner's - it neither rotates the owner's session
    /// logs nor has to end the owner's running copy, because SP-0118's instance identity makes a relocated profile an
    /// instance of its own. Undocumented to users on purpose: environment rather than a command-line option for the
    /// same reason as <see cref="SmokeRecording"/> - the launch contract is shared with the shell integration and must
    /// not grow a test switch.
    /// </summary>
    internal const string DataDirectoryVariable = "STREAMSPLAYER_DATA_DIRECTORY";

    /// <summary><c>%LOCALAPPDATA%\StreamsPlayer</c> - where an ordinary installation keeps its state.</summary>
    internal static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamsPlayer");

    /// <summary>
    /// What <see cref="DataDirectoryVariable"/> held, or null when it was absent or empty. Kept so startup can log a
    /// value it refused - a gate whose relocation silently failed would be running against the owner's profile.
    /// </summary>
    internal static string? RequestedDataDirectory { get; } = ReadRequested();

    /// <summary>
    /// The directory this process uses: <see cref="DefaultDataDirectory"/>, unless <see cref="DataDirectoryVariable"/>
    /// names a fully qualified path. A relative or malformed value is refused rather than resolved against the
    /// working directory, which for a shortcut launch is anyone's guess.
    /// </summary>
    internal static string DataDirectory { get; } = Resolve(RequestedDataDirectory) ?? DefaultDataDirectory;

    /// <summary>True when this process runs against a relocated profile.</summary>
    internal static bool IsRelocated => !string.Equals(DataDirectory, DefaultDataDirectory, StringComparison.Ordinal);

    private static string? ReadRequested()
    {
        var raw = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static string? Resolve(string? requested)
    {
        if (requested is null || !Path.IsPathFullyQualified(requested))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(requested);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
