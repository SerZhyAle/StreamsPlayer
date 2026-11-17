using System.Diagnostics;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0275: where this process keeps its state, and so which running copy it belongs to. The pipe and the lock a command
/// is sent to carry a profile qualifier when the data directory is relocated (<see cref="SingleInstanceIdentity"/>), so the
/// player and its console companion have to resolve the directory the same way or the companion would talk to another
/// copy than the one the player runs as. It is declared once here; <c>AppPaths</c> in the application delegates to it.
/// </summary>
public static class AppDataProfile
{
    /// <summary>
    /// SP-0133: relocates the data directory for one process. Set only by <c>scripts/smoke-playback.ps1</c>, so the playback
    /// gate runs against a throw-away profile instead of the owner's. Undocumented to users on purpose: environment rather
    /// than a command-line option, because the launch contract is shared with the shell integration and must not grow a
    /// test switch.
    /// </summary>
    public const string Variable = "STREAMSPLAYER_DATA_DIRECTORY";

    /// <summary><c>%LOCALAPPDATA%\StreamsPlayer</c> - where an ordinary installation keeps its state.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamsPlayer");

    /// <summary>
    /// What <see cref="Variable"/> held, or null when it was absent or empty. Kept so startup can log a value it refused - a
    /// gate whose relocation silently failed would be running against the owner's profile.
    /// </summary>
    public static string? Requested { get; } = ReadRequested();

    /// <summary>
    /// The directory this process uses: <see cref="DefaultDirectory"/>, unless <see cref="Variable"/> names a fully
    /// qualified path. A relative or malformed value is refused rather than resolved against the working directory, which
    /// for a shortcut launch is anyone's guess.
    /// </summary>
    public static string DataDirectory { get; } = Resolve(Requested) ?? DefaultDirectory;

    /// <summary>True when this process runs against a relocated profile.</summary>
    public static bool IsRelocated => !string.Equals(DataDirectory, DefaultDirectory, StringComparison.Ordinal);

    /// <summary>The lock and pipe names of the copy that runs with this profile in this Windows session.</summary>
    public static SingleInstanceIdentity Identity()
    {
        using var process = Process.GetCurrentProcess();
        return SingleInstanceIdentity.For(DataDirectory, DefaultDirectory, process.SessionId);
    }

    private static string? ReadRequested()
    {
        var raw = Environment.GetEnvironmentVariable(Variable);
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
