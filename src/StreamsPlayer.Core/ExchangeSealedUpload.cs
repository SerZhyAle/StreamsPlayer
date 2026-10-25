namespace StreamsPlayer.Core;

/// <summary>
/// The body of a relay upload after `upload-accepted` (`DEVICE-EXCHANGE` 7.6 and item DL): for a sealed broadcast the
/// header chunk first - the key ring, then the format header's record when the format has one - and then every media
/// chunk as one sealed record; for a broadcast that is not sealed, the media chunks as they are. The framing of a chunk
/// (length, flag, payload) stays in <see cref="ExchangeUploadChunks"/>; the clean end is the caller's, because a retired
/// upload (item N) must close without it.
/// </summary>
public static class ExchangeSealedUpload
{
    /// <param name="stream">The data stream, after `upload-accepted`.</param>
    /// <param name="relay">The broadcast's sealed relay, or null for an upload that is not sealed (a server without `sealed` is sent nothing, so this is for tests of the older shape only).</param>
    /// <param name="formatHeader">PAT and PMT for MPEG-TS; empty for ADTS.</param>
    /// <param name="chunks">The media chunks: whole ADTS frames, or MPEG-TS from an IDR access unit - each joinable.</param>
    /// <param name="headerWritten">Raised once the header chunk is on the stream (a replacement upload waits for it).</param>
    public static Task WriteAsync(
        Stream stream, ExchangeSealedRelayEncoder? relay, ReadOnlyMemory<byte> formatHeader,
        IAsyncEnumerable<byte[]> chunks, Action? headerWritten, CancellationToken cancellationToken) =>
        WriteAsync(stream, relay, formatHeader, AudioChunks(chunks), headerWritten, cancellationToken);

    public static async Task WriteAsync(
        Stream stream, ExchangeSealedRelayEncoder? relay, ReadOnlyMemory<byte> formatHeader,
        IAsyncEnumerable<BroadcastMediaChunk> chunks, Action? headerWritten, CancellationToken cancellationToken)
    {
        if (relay is not null)
        {
            await ExchangeUploadChunks.WriteAsync(stream, ExchangeUploadChunks.Header, relay.HeaderChunk(formatHeader.Span),
                cancellationToken).ConfigureAwait(false);
        }
        else if (!formatHeader.IsEmpty)
        {
            await ExchangeUploadChunks.WriteAsync(stream, ExchangeUploadChunks.Header, formatHeader,
                cancellationToken).ConfigureAwait(false);
        }

        headerWritten?.Invoke();
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var flags = chunk.Joinable ? ExchangeUploadChunks.Joinable : (byte)0;
            var payload = relay is null ? chunk.Payload : relay.SealChunk(flags, chunk.Payload);
            await ExchangeUploadChunks.WriteAsync(stream, flags, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<BroadcastMediaChunk> AudioChunks(IAsyncEnumerable<byte[]> chunks)
    {
        await foreach (var chunk in chunks.ConfigureAwait(false)) yield return new(chunk, true);
    }
}
