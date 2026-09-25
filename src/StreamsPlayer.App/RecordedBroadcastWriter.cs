using System.IO;
using System.Runtime.InteropServices;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0101: the recordings folder and unique file naming for recorded broadcasts. Staging is SP-0121's <see cref="RecordingStaging"/>.
/// </summary>
internal static class RecordedBroadcastWriter
{
    private const int MaxNameAttempts = 100;

    /// <summary>
    /// The folder recordings are written to: what the user chose, or the Downloads known folder when unset.
    /// </summary>
    internal static string ResolveFolder(string? configuredFolder) =>
        string.IsNullOrWhiteSpace(configuredFolder) ? DownloadsFolder() : configuredFolder.Trim();

    /// <summary>
    /// Two recordings inside one second share a name, so the second one takes a numeric suffix rather
    /// than overwriting the first.
    /// </summary>
    internal static string ReserveUniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var attempt = 2; attempt <= MaxNameAttempts; attempt++)
        {
            candidate = Path.Combine(folder, $"{stem}-{attempt}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"No free file name for '{stem}' in '{folder}'.");
    }

    /// <summary>
    /// The Downloads known folder.
    /// </summary>
    internal static string DownloadsFolder()
    {
        try
        {
            var hr = SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var path);
            if (hr == 0 && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (DllNotFoundException)
        {
            // Fall through to the profile-relative guess.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
        uint flags,
        IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
