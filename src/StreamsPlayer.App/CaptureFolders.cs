using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>The folder a capture will be written to, the chosen folder it replaced, and the folders still behind it.</summary>
/// <param name="Folder">The first folder of the chain that accepted a write.</param>
/// <param name="Skipped">The first folder of the chain when <paramref name="Folder"/> is not it - the fallback the user is told about.</param>
/// <param name="Rest">The folders after <paramref name="Folder"/>, for a write that fails later.</param>
internal sealed record CaptureDestination(string Folder, string? Skipped, IReadOnlyList<string> Rest);

/// <summary>
/// SP-0179: the Windows side of CAPTURE-OUTPUT rules 9-11 - the known folders each kind defaults to, the chain a
/// capture walks, and the probe that tells a writable folder from one that is not. The chain's order itself is
/// <see cref="CaptureFolderChain"/>'s, in Core.
/// </summary>
internal static class CaptureFolders
{
    private static readonly Guid PicturesId = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid VideosId = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    private static readonly Guid MusicId = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The role default for <paramref name="kind"/> (rule 9): frames in Pictures, recordings in Videos or Music.</summary>
    internal static string Default(CaptureKind kind) => kind switch
    {
        CaptureKind.VideoFrame => Path.Combine(KnownFolder(PicturesId, "Pictures"), "Frames"),
        CaptureKind.StreamVideo => Path.Combine(KnownFolder(VideosId, "Videos"), "Recordings"),
        CaptureKind.StreamAudio => Path.Combine(KnownFolder(MusicId, "Music"), "Recordings"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>The Downloads known folder - the last link of every chain (rule 11).</summary>
    internal static string Downloads() => KnownFolder(DownloadsId, "Downloads");

    /// <summary>The folders a capture of <paramref name="kind"/> tries, in order; <paramref name="chosen"/> is the user's folder or null.</summary>
    internal static IReadOnlyList<string> Chain(CaptureKind kind, string? chosen) =>
        CaptureFolderChain.For(chosen, Default(kind), Downloads());

    /// <summary>
    /// The first folder of <paramref name="chain"/> that can be created and written, or null when none can. Touches
    /// the disk, so it runs off the UI thread: a folder on a sleeping or unplugged drive can take seconds to refuse.
    /// </summary>
    internal static CaptureDestination? FirstWritable(IReadOnlyList<string> chain)
    {
        for (var index = 0; index < chain.Count; index++)
        {
            if (CanWrite(chain[index]))
            {
                return new CaptureDestination(chain[index], index == 0 ? null : chain[0], chain.Skip(index + 1).ToList());
            }
        }

        return null;
    }

    /// <summary>
    /// The full path of the first of <paramref name="fileName"/>, <c>(2)</c>, <c>(3)</c> .. that is free in
    /// <paramref name="folder"/> (rules 5-6). A folder full of one name is an ordinary write failure.
    /// </summary>
    internal static string ReserveUniquePath(string folder, string fileName)
    {
        var free = CaptureFileName.FirstFree(fileName, name => File.Exists(Path.Combine(folder, name)));
        return free is null
            ? throw new IOException($"No free file name for '{fileName}' in '{folder}'.")
            : Path.Combine(folder, free);
    }

    /// <summary>Whether an exception is the file system refusing a folder - the failures a chain moves past.</summary>
    internal static bool IsFolderRefusal(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

    /// <summary>
    /// Creates the folder and a file in it that deletes itself on close. Existence alone is not enough: a read-only
    /// share or a full volume exists and still refuses the capture.
    /// </summary>
    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $"~probe-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (IsFolderRefusal(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// A known folder, asked of Windows rather than assembled from the profile path, because these folders are
    /// commonly redirected to another drive; the profile guess is only the fallback for the call failing.
    /// </summary>
    private static string KnownFolder(Guid id, string profileSubfolder)
    {
        try
        {
            var hr = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path);
            if (hr == 0 && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (DllNotFoundException)
        {
            // Fall through to the profile-relative guess.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), profileSubfolder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
        uint flags,
        IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
