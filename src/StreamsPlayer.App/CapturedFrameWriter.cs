using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0038: writes a captured video frame into the user's own file system. Lives in the App layer, not
/// Core, because it depends on WPF imaging and on Windows known folders.
/// </summary>
internal static class CapturedFrameWriter
{
    /// <summary>
    /// JPEG quality. 75 is the point where a live frame stops gaining visible detail per byte; a
    /// full-resolution frame at this setting is a few hundred kilobytes rather than a few megabytes.
    /// </summary>
    private const int JpegQuality = 75;

    /// <summary>Bound on the same-second disambiguation suffix; a longer run means something else is wrong.</summary>
    private const int MaxNameAttempts = 100;

    /// <summary>
    /// Encodes and writes the frame, returning the full path written. The frame must be frozen: it is
    /// encoded on a worker thread. Throws the ordinary file-system exceptions, which the caller reports.
    /// </summary>
    internal static async Task<string> SaveAsync(
        BitmapSource frame,
        string? configuredFolder,
        string? channelTitle,
        DateTimeOffset capturedAt)
    {
        var folder = ResolveFolder(configuredFolder);
        return await Task.Run(() =>
        {
            // The encoder is built here, not on the caller's thread: BitmapEncoder is a DispatcherObject,
            // and one created on the UI thread refuses to Save from a worker - which wrote an empty file
            // and lost the exception into an unobserved task. A frozen frame is thread-safe to encode.
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(frame));
            Directory.CreateDirectory(folder);
            var path = ReserveUniquePath(folder, CapturedFrameName.For(channelTitle, capturedAt));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        });
    }

    /// <summary>
    /// The folder frames are written to: what the user chose, or the Downloads known folder when the
    /// setting is unset. Resolved on every save so moving Downloads, or clearing the setting, takes
    /// effect without touching stored state.
    /// </summary>
    internal static string ResolveFolder(string? configuredFolder) =>
        string.IsNullOrWhiteSpace(configuredFolder) ? DownloadsFolder() : configuredFolder.Trim();

    /// <summary>
    /// Two captures inside one second share a name, and the timestamp has no finer field, so the second
    /// one takes a numeric suffix rather than overwriting the first.
    /// </summary>
    private static string ReserveUniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var attempt = 2; attempt <= MaxNameAttempts; attempt++)
        {
            candidate = Path.Combine(folder, $"{stem}-{attempt}{CapturedFrameName.Extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"No free file name for '{stem}' in '{folder}'.");
    }

    /// <summary>
    /// The Downloads known folder. Asked of Windows rather than assembled from the profile path,
    /// because Downloads is commonly redirected to another drive; the profile guess is only the
    /// fallback for the call failing.
    /// </summary>
    private static string DownloadsFolder()
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
