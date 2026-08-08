using System.Net.Http;

namespace StreamsPlayer.Core;

/// <summary>How far a download has got, and how far it has to go when the server says.</summary>
/// <remarks>
/// Deliberately the same shape as <see cref="FFmpegInstallProgress"/>, including <c>null</c> as the
/// representation of "the server declared no length". The two are not unified: that struct is a
/// published shape with its own consumers and tests, and merging them would be a rename-only refactor
/// of a feature this change does not touch.
/// </remarks>
public readonly record struct DownloadProgress(long ReceivedBytes, long? TotalBytes)
{
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// SP-0056: the one chunked read loop every large download in the core uses. Buffering a response body
/// with <c>ReadAsByteArrayAsync</c> reports nothing, cannot be interrupted once the read has begun, and
/// cannot tell a slow transfer from a dead one; this gives all three properties at once.
/// </summary>
public static class HttpDownload
{
    private const int BufferBytes = 128 * 1024;

    /// <summary>Reads the whole body, reporting as it goes.</summary>
    /// <param name="ceilingBytes">
    /// Refuse a body larger than this, checked against both the declared length and the bytes that
    /// actually arrive. <c>null</c> accepts any size.
    /// </param>
    /// <param name="idleTimeout">
    /// How long the transfer may deliver nothing before it is abandoned. This bounds *silence*, not
    /// duration: a slow link that keeps sending finishes however long it takes, while a socket that
    /// stops answering fails promptly instead of hanging until some wall-clock deadline.
    /// </param>
    /// <exception cref="InvalidDataException"><paramref name="ceilingBytes"/> was exceeded.</exception>
    /// <exception cref="TimeoutException">The transfer went silent for <paramref name="idleTimeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<byte[]> ReadAllBytesAsync(
        HttpResponseMessage response,
        IProgress<DownloadProgress>? progress,
        long? ceilingBytes,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentLength;
        if (ceilingBytes is not null && declared > ceilingBytes)
        {
            throw new InvalidDataException(
                $"The download declares {declared} bytes, above the {ceilingBytes} byte ceiling.");
        }

        // Before the first chunk, so a caller can show a total and switch to a determinate display
        // immediately rather than after the first buffer's worth has arrived.
        progress?.Report(new DownloadProgress(0, declared));

        using var buffer = new MemoryStream(PreSize(declared));
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);

        var chunk = new byte[BufferBytes];
        long received = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(chunk, idle.Token);
                if (read <= 0)
                {
                    break;
                }

                // Rescheduling on every chunk is what makes the bound a silence bound. CancelAfter on an
                // already-armed source replaces the pending timer rather than adding a second one.
                idle.CancelAfter(idleTimeout);
                received += read;
                // A server may under-declare or omit the length, so the ceiling is enforced on the bytes
                // actually arriving and not only on the header.
                if (ceilingBytes is not null && received > ceilingBytes)
                {
                    throw new InvalidDataException(
                        $"The download exceeded the {ceilingBytes} byte ceiling.");
                }

                buffer.Write(chunk, 0, read);
                progress?.Report(new DownloadProgress(received, declared));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked source fires for two reasons and the caller has to tell them apart: an abandoned
            // download is the user's choice and must not read as an error, while a transfer that stopped
            // delivering is a genuine failure. TimeoutException rather than any OperationCanceledException
            // is what keeps the second case out of the caller's "cancelled" branch.
            throw new TimeoutException(
                $"The download delivered nothing for {idleTimeout.TotalSeconds:0} seconds.");
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Capacity for the accumulating buffer. A declared length saves the doubling ladder on a body of the
    /// size these callers actually fetch, but it is a value a remote server chose, so it is capped: an
    /// over-declared length must not turn into a matching allocation, and growing from the cap is cheap
    /// next to the transfer that would have to arrive to reach it.
    /// </summary>
    private static int PreSize(long? declaredLength) =>
        declaredLength is > 0 ? (int)Math.Min(declaredLength.Value, MaximumPreSizeBytes) : 0;

    private const int MaximumPreSizeBytes = 64 * 1024 * 1024;
}
