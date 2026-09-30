using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0177: the local-file side of M3U import and export, held to the same terms as the URL side - the same
/// read ceiling, the same strict UTF-8 - and written so a failed export never costs the previous one.
/// </summary>
public static class M3uPlaylistFile
{
    /// <exception cref="InvalidDataException">The file is larger than <see cref="M3uImportService.MaximumPlaylistBytes"/>.</exception>
    /// <exception cref="DecoderFallbackException">The file is not valid UTF-8.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        // The open file dialog offers "All files", so this is the only thing between a picked video file and
        // reading it whole into memory. The length is checked again while reading, for a file still growing.
        if (stream.Length > M3uImportService.MaximumPlaylistBytes)
        {
            throw TooLarge();
        }

        using var buffer = new MemoryStream((int)stream.Length);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > M3uImportService.MaximumPlaylistBytes)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return M3uImportService.DecodeUtf8(buffer.ToArray());
    }

    /// <summary>Writes the playlist beside <paramref name="path"/> first and moves it into place.</summary>
    /// <exception cref="IOException">The file could not be written; any previous file is intact.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or file is not writable; any previous file is intact.</exception>
    public static Task WriteAsync(string path, IEnumerable<StreamChannel> channels, CancellationToken cancellationToken = default)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(M3uPlaylistWriter.Write(channels));
        return WriteAtomicAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken);
    }

    /// <summary>Tests substitute a writer that fails midway; the product always writes the whole body.</summary>
    internal static async Task WriteAtomicAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await DurableFile.ReplaceAsync(
                fullPath,
                temporaryPath,
                (stream, token) => write(stream, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static InvalidDataException TooLarge() =>
        new($"The playlist file is larger than {M3uImportService.MaximumPlaylistBytes} bytes.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original failure is the one worth reporting; a stranded hidden temp file is harmless.
        }
    }
}
