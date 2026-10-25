using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// S13-1: disposing the sealed relay encoder zeroes its epoch key, and an all-zero key is still a valid AES key, so a
/// chunk sealed afterwards would be readable by anyone who guesses it. The encoder must refuse instead, and an upload
/// that races the teardown must end without writing that chunk.
/// </summary>
public sealed class ExchangeSealedRelayDisposeTests
{
    [Fact]
    public async Task Encoder_AfterDisposeSealsNothingAndTheUploadWritesNoChunk()
    {
        var encoder = new ExchangeSealedRelayEncoder("disposed", "audio/aac");
        encoder.NewGroupKey("own");
        // One header frame is what the upload may write before the teardown: 4 length bytes, 1 flag byte, the key ring.
        var headerFrameLength = 5 + encoder.HeaderChunk([]).Length;

        using var upload = new MemoryStream();
        await ExchangeSealedUpload.WriteAsync(
            upload, encoder, ReadOnlyMemory<byte>.Empty, MediaAfterTeardown(encoder), null, CancellationToken.None);

        Assert.Equal(headerFrameLength, upload.Length);
        Assert.Throws<ObjectDisposedException>(() => encoder.SealChunk(ExchangeUploadChunks.Joinable, [1, 2, 3]));
    }

    // The producer's teardown disposes the sealer while the pump still has a chunk in hand.
    private static async IAsyncEnumerable<byte[]> MediaAfterTeardown(ExchangeSealedRelayEncoder encoder)
    {
        await Task.Yield();
        encoder.Dispose();
        yield return new byte[] { 1, 2, 3 };
    }
}
