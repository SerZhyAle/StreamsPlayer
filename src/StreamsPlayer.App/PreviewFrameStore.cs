using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace StreamsPlayer.App;

public sealed class PreviewFrameStore(string directory, int capacity, int jpegQuality)
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
        try
        {
            Directory.CreateDirectory(directory);
            var path = ResolvePath(url);
            var bytes = await Task.Run(() => Encode(frame), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            await TrimAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A later sweep can retry the non-essential disk cache.
        }
        catch (UnauthorizedAccessException)
        {
            // Grid previews remain available in memory when local storage is unavailable.
        }
        catch (NotSupportedException)
        {
            // An unsupported encoder does not affect the live in-memory frame.
        }
    }

    public string ResolvePath(string url) => Path.Combine(directory, $"{HashUrl(url)}.jpg");

    private byte[] Encode(BitmapSource frame)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private Task TrimAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var files = new DirectoryInfo(directory)
            .EnumerateFiles("*.jpg", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(capacity)
            .ToList();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    }, cancellationToken);

    private static string HashUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
}
