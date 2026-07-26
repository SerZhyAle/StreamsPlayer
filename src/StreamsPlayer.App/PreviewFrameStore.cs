using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace StreamsPlayer.App;

public sealed class PreviewFrameStore(string directory, long maxBytes, int jpegQuality)
{
    public async Task<BitmapSource?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        var path = ResolvePath(url);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string url, BitmapSource frame, CancellationToken cancellationToken)
    {
        if (await WriteAsync(url, frame, cancellationToken).ConfigureAwait(false))
        {
            await TrimOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes one frame and reports whether it landed, without trimming the store. The SP-0031 atlas
    /// import seeds ~1900 frames back to back and <see cref="TrimOnce"/> enumerates the whole directory,
    /// so trimming per frame would be quadratic; the bulk caller trims once at the end.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose. WPF imaging objects are thread-affine unless every object in the chain is
    /// frozen, and a <c>CroppedBitmap</c> over a decoded frame reaches back to its <c>BitmapDecoder</c>,
    /// which is not. An <c>await</c> between imaging calls lets the continuation resume on another pool
    /// thread and the next crop throws "the calling thread cannot access this object"; the bulk caller
    /// therefore does all of its imaging on one thread, with no await in between.
    /// </remarks>
    public bool Write(string url, BitmapSource frame)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = ResolvePath(url);
            File.WriteAllBytes(path, Encode(frame));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (IOException)
        {
            // A later sweep can retry the non-essential disk cache.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Grid previews remain available in memory when local storage is unavailable.
            return false;
        }
        catch (NotSupportedException)
        {
            // An unsupported encoder does not affect the live in-memory frame.
            return false;
        }
    }

    /// <summary>Off-thread wrapper over <see cref="Write"/> for the single-frame capture path.</summary>
    public Task<bool> WriteAsync(string url, BitmapSource frame, CancellationToken cancellationToken) =>
        Task.Run(() => Write(url, frame), cancellationToken);

    /// <summary>True when this URL already has a stored frame, so a bulk seed can leave it alone.</summary>
    public bool Exists(string url) => File.Exists(ResolvePath(url));

    public string ResolvePath(string url) => Path.Combine(directory, $"{HashUrl(url)}.jpg");

    private byte[] Encode(BitmapSource frame)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Off-thread wrapper over <see cref="TrimOnce"/>.</summary>
    public Task TrimOnceAsync(CancellationToken cancellationToken) =>
        Task.Run(() => TrimOnce(cancellationToken), cancellationToken);

    /// <summary>
    /// Enforces the disk budget once, newest-first. Call after a batch, not per frame: this walks the
    /// entire directory.
    /// </summary>
    public void TrimOnce(CancellationToken cancellationToken)
    {
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(directory)
                .EnumerateFiles("*.jpg", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return; // nothing written yet; nothing to trim
        }
        catch (UnauthorizedAccessException)
        {
            return; // the store is unreadable; the in-memory previews are unaffected
        }

        long retained = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            retained += file.Length;
            if (retained <= maxBytes)
            {
                continue;
            }

            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Cleanup is retried after the next successful frame write.
            }
            catch (UnauthorizedAccessException)
            {
                // A locked cache entry is harmless and can be retried later.
            }
        }
    }

    private static string HashUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
}
