using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media.Imaging;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0038 / SP-0179: encodes a captured video frame and writes it into the first folder of its chain that takes
/// it. Lives in the App layer, not Core, because it depends on WPF imaging. Where the chain comes from is
/// <see cref="CaptureFolders"/>'s business.
/// </summary>
internal static class CapturedFrameWriter
{
    /// <summary>
    /// JPEG quality. 75 is the point where a live frame stops gaining visible detail per byte; a
    /// full-resolution frame at this setting is a few hundred kilobytes rather than a few megabytes.
    /// </summary>
    private const int JpegQuality = 75;

    /// <summary>Saves racing for one name in one second; a longer run means something else is wrong.</summary>
    private const int MaxMoveAttempts = 5;

    /// <summary>EXIF <c>DateTimeOriginal</c> (tag 36867), the capture time inside the file, CAPTURE-OUTPUT rule 16.</summary>
    private const string DateTimeOriginalQuery = "/app1/ifd/exif/{ushort=36867}";

    /// <summary>
    /// Encodes the frame and writes it into the first folder of <paramref name="chain"/> that accepts it, returning
    /// the full path written and, when that is not the chain's first folder, the folder that refused it (rule 11:
    /// the user is told in the same moment). The frame must be frozen: it is encoded on a worker thread. When every
    /// folder refuses, the last refusal is thrown for the caller to report.
    /// </summary>
    internal static Task<(string Path, string? Skipped)> SaveAsync(
        BitmapSource frame,
        IReadOnlyList<string> chain,
        string? channelTitle,
        DateTimeOffset capturedAt) => Task.Run(() =>
    {
        // Encoded once, before any folder is tried: an encoder saves only once, and a frame that cannot be encoded
        // is no folder's fault.
        var bytes = Encode(frame, capturedAt);
        var fileName = CaptureFileName.For(CaptureKind.VideoFrame, capturedAt, channelTitle);
        ExceptionDispatchInfo? last = null;
        foreach (var folder in chain)
        {
            try
            {
                var path = WriteInto(folder, fileName, bytes);
                return (path, ReferenceEquals(folder, chain[0]) ? null : chain[0]);
            }
            catch (Exception exception) when (CaptureFolders.IsFolderRefusal(exception))
            {
                last = ExceptionDispatchInfo.Capture(exception);
            }
        }

        last?.Throw();
        throw new IOException("No folder to save the frame in.");
    });

    private static byte[] Encode(BitmapSource frame, DateTimeOffset capturedAt)
    {
        // The encoder is built here, on the worker, not on the caller's thread: BitmapEncoder is a DispatcherObject,
        // and one created on the UI thread refuses to Save from a worker - which wrote an empty file and lost the
        // exception into an unobserved task. A frozen frame is thread-safe to encode.
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery(DateTimeOriginalQuery, capturedAt.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the frame under a temporary name in <paramref name="folder"/> and renames it into place, so the
    /// frame is visible only once it is complete (CAPTURE-OUTPUT rule 12). A failure deletes the temporary: a
    /// full disk or a folder that went away must not leave a truncated picture behind (SP-0121 R7).
    /// </summary>
    private static string WriteInto(string folder, string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        for (var attempt = 1; ; attempt++)
        {
            var destination = CaptureFolders.ReserveUniquePath(folder, fileName);
            var temporary = Path.Combine(folder, $"~{Path.GetFileName(destination)}.partial");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                }

                File.Move(temporary, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (attempt < MaxMoveAttempts && File.Exists(destination))
            {
                // Lost the name to another save in the same second; reserve the next one.
                TryDelete(temporary);
            }
            catch (Exception exception) when (CaptureFolders.IsFolderRefusal(exception))
            {
                TryDelete(temporary);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done from here; the caller still reports the save as failed.
        }
    }
}
