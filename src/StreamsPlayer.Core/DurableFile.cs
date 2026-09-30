using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>Whether a user file was read, and what that means for writing it (SP-0175).</summary>
internal enum FileReadStatus
{
    /// <summary>
    /// No file, or a zero-byte one: there is nothing on disk to lose, so a save may create the file freely.
    /// </summary>
    Absent,

    /// <summary>The file was read. Its content is in memory, so a save replaces a known state.</summary>
    Read,

    /// <summary>
    /// The file exists and could not be read - locked, denied, or unparseable. Its content is unknown, so
    /// nothing may save over it until a read has succeeded.
    /// </summary>
    Unreadable
}

/// <summary>
/// The two halves of SP-0175's persistence rule for the small user files beside the catalog: a save is
/// durable before it replaces the previous file, and a read that failed says so instead of reading as
/// "no file".
/// </summary>
internal static class DurableFile
{
    /// <summary>
    /// Writes <paramref name="temporaryPath"/> through <paramref name="write"/>, flushes it to the disk,
    /// and only then renames it over <paramref name="destinationPath"/>.
    /// </summary>
    /// <remarks>
    /// NTFS journals the rename, not the data: a temp file flushed only to the .NET buffer can be moved
    /// over the real file while its bytes are still only in the write cache, and a power cut in that
    /// window leaves the destination zero-filled - for the catalog state, every channel, collection and
    /// pin the user had. <see cref="FileOptions.WriteThrough"/> sends each write to the disk as it
    /// happens, and <c>Flush(flushToDisk: true)</c> also flushes the file's metadata, so the rename that
    /// follows can never be journalled ahead of the data it names.
    /// <para>Throws as the underlying I/O throws; the caller owns the temp file's cleanup on failure.</para>
    /// </remarks>
    internal static async Task ReplaceAsync(
        string destinationPath,
        string temporaryPath,
        Func<FileStream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(
                         temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await write(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Reads and parses a user file, reporting which of the three states it is in rather than collapsing
    /// "unreadable" into "absent". Cancellation is the one failure that says nothing about the file and
    /// is allowed to escape; any other failure is returned as <paramref name="status"/>
    /// <see cref="FileReadStatus.Unreadable"/> with its exception, so a caller that rethrows can still
    /// log the real cause.
    /// </summary>
    internal static async Task<(T? Value, FileReadStatus Status, Exception? Failure)> ReadAsync<T>(
        string path,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return (null, FileReadStatus.Absent, null);
            }

            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer
                .DeserializeAsync<T>(stream, jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return (value, FileReadStatus.Read, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, FileReadStatus.Unreadable, exception);
        }
    }
}
