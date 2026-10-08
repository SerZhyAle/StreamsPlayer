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

    /// <summary>
    /// SP-0129: sends <paramref name="request"/> and waits for the response head for at most
    /// <paramref name="headerTimeout"/>, whatever the client's own <see cref="HttpClient.Timeout"/> is.
    /// </summary>
    /// <remarks>
    /// The body is deliberately not covered: <see cref="HttpClient.Timeout"/> stops applying once the head
    /// has arrived (proved by <c>HttpClientTimeoutPremiseTests</c>), so every body read has to carry its own
    /// bound - <see cref="ReadAllBytesAsync"/> and <see cref="CopyToAsync"/> give it a silence bound. The
    /// clients the long downloads use have an infinite timeout, which left a TLS or proxy stall before the
    /// head bounded only by the user's Cancel.
    /// </remarks>
    /// <exception cref="TimeoutException">No response head arrived within <paramref name="headerTimeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<HttpResponseMessage> SendForHeadersAsync(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan headerTimeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(headerTimeout);
        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Same split as the body loop: the user's Cancel stays a cancellation, our own deadline is a
            // failure. The client's own timeout lands here too, which is the same fact reported the same way.
            throw new TimeoutException(
                $"The server sent no response for {headerTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Convenience GET form of <see cref="SendForHeadersAsync(HttpClient, HttpRequestMessage, TimeSpan, CancellationToken)"/>.</summary>
    public static async Task<HttpResponseMessage> GetForHeadersAsync(
        HttpClient client,
        Uri url,
        TimeSpan headerTimeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendForHeadersAsync(client, request, headerTimeout, cancellationToken).ConfigureAwait(false);
    }

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
    /// <exception cref="HttpIOException">The body ended before its declared length.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<byte[]> ReadAllBytesAsync(
        HttpResponseMessage response,
        IProgress<DownloadProgress>? progress,
        long? ceilingBytes,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(PreSize(response.Content.Headers.ContentLength, ceilingBytes));
        await CopyToAsync(response, buffer, progress, ceilingBytes, idleTimeout, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// SP-0128: the same loop as <see cref="ReadAllBytesAsync"/>, streaming into
    /// <paramref name="destination"/> for a body too large to hold in memory. Same ceiling, silence bound,
    /// short-read check and exceptions.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public static async Task<long> CopyToAsync(
        HttpResponseMessage response,
        Stream destination,
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

                await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
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

        // SP-0107: a body that ends before its declared length is the "short read" STREAM-BANK rule 11
        // names as part of the publish window. The socket handler usually raises this itself, but not every
        // handler does, and returning the short buffer would hand the caller a truncated file to fail on for
        // a less telling reason. Raised as the transport's own type so one classification covers both.
        if (declared is not null && received < declared)
        {
            throw new HttpIOException(
                HttpRequestError.ResponseEnded,
                $"The download ended after {received} of the {declared} declared bytes.");
        }

        return received;
    }

    /// <summary>
    /// Capacity for the accumulating buffer. A declared length saves the doubling ladder on a body of the
    /// size these callers actually fetch, but it is a value a remote server chose, so it is capped: an
    /// over-declared length must not turn into a matching allocation, and growing from the cap is cheap
    /// next to the transfer that would have to arrive to reach it.
    /// </summary>
    /// <remarks>
    /// Also capped by <paramref name="ceilingBytes"/>: <see cref="ReadAllBytesAsync"/> sizes the buffer
    /// before <see cref="CopyToAsync"/> refuses a body that declares more than the ceiling, so without
    /// this a refused body would still have cost a cap-sized allocation first.
    /// </remarks>
    internal static int PreSize(long? declaredLength, long? ceilingBytes = null)
    {
        if (declaredLength is not > 0)
        {
            return 0;
        }

        var size = Math.Min(declaredLength.Value, MaximumPreSizeBytes);
        if (ceilingBytes is not null)
        {
            size = Math.Min(size, Math.Max(ceilingBytes.Value, 0));
        }

        return (int)size;
    }

    private const int MaximumPreSizeBytes = 64 * 1024 * 1024;
}
