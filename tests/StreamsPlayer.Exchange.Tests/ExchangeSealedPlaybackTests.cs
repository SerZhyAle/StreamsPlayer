using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0226 / DEVICE-EXCHANGE items DL and DM, end to end on loopback sockets: a sealed relay played through the proxy
/// (the player gets clear media and the true type, the server's side sees none), an epoch this listener does not hold
/// (one reconnect), a wrong key (nothing played), and a sealed tunnel through the forwarder (TLS inside, one pinned leaf).
/// </summary>
public sealed class ExchangeSealedPlaybackTests
{
    private const string RelayUrl = "https://relay.example.net:44022/v2/b/b1/stream";

    // ------------------------------------------------------------------ helpers

    private static byte[] TransportStream(int packets)
    {
        var bytes = new byte[188 * packets];
        for (var packet = 0; packet < packets; packet++)
        {
            bytes[packet * 188] = 0x47;
            for (var index = 1; index < 188; index++)
            {
                bytes[packet * 188 + index] = (byte)(packet * 31 + index);
            }
        }

        return bytes;
    }

    private static byte[] Join(IEnumerable<byte[]> parts) => parts.SelectMany(part => part).ToArray();

    private static async Task WriteChunkedAsync(Stream stream, byte[] body, int piece)
    {
        for (var offset = 0; offset < body.Length; offset += piece)
        {
            var take = Math.Min(piece, body.Length - offset);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(take.ToString("x") + "\r\n"));
            await stream.WriteAsync(body.AsMemory(offset, take));
            await stream.WriteAsync("\r\n"u8.ToArray());
            await stream.FlushAsync();
        }
    }

    private static async Task ReadRequestAsync(Stream stream)
    {
        var buffer = new byte[1];
        var tail = new List<byte>();
        while (!(tail.Count >= 4 && tail[^4] == '\r' && tail[^3] == '\n' && tail[^2] == '\r' && tail[^1] == '\n'))
        {
            if (await stream.ReadAsync(buffer) == 0)
            {
                return;
            }

            tail.Add(buffer[0]);
        }
    }

    private sealed class FakeRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<int, Stream, Task<byte[]>> _script;
        private int _connections;

        /// <summary>Everything the relay put on the wire, per connection: what a server's operator would see.</summary>
        internal List<byte[]> Wire { get; } = [];

        internal int Connections => Volatile.Read(ref _connections);

        internal FakeRelay(Func<int, Stream, Task<byte[]>> script)
        {
            _script = script;
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        var number = Interlocked.Increment(ref _connections);
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                var stream = client.GetStream();
                                await ReadRequestAsync(stream);
                                var sent = await _script(number, stream);
                                lock (Wire)
                                {
                                    Wire.Add(sent);
                                }
                            }
                        });
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    // The listener closed.
                }
            });
        }

        internal Task<Stream> Connect(string host, int port, string? fingerprint, CancellationToken token)
        {
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            return Task.FromResult<Stream>(client.GetStream());
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<(string Head, byte[] Body)> PlayAsync(ExchangeRelayProxy proxy)
    {
        using var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, proxy.BoundPort);
        var stream = player.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {new Uri(proxy.LoopbackUrl).AbsolutePath} HTTP/1.1\r\nHost: x\r\n\r\n"));
        using var all = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await stream.CopyToAsync(all, timeout.Token);
        var bytes = all.ToArray();
        var end = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        return end < 0 ? (Encoding.ASCII.GetString(bytes), []) : (Encoding.ASCII.GetString(bytes, 0, end), bytes[(end + 4)..]);
    }

    private static string RelayHead(string type = "application/x-fmsx-sealed") =>
        $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nTransfer-Encoding: chunked\r\nCache-Control: no-cache\r\n\r\n";

    private static ExchangeRelayProxy Proxy(FakeRelay relay, byte[] groupKey) =>
        ExchangeRelayProxy.Start(RelayUrl, null, relay.Connect, null, new ExchangeSealedRelayOptions("b1", groupKey));

    // ------------------------------------------------------------------ the sealed relay

    [Fact]
    public async Task AStalledSiblingDoesNotBlockAnotherSealedListenerOrItsStop()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "video/mp2t");
        var key = encoder.NewGroupKey("own");
        var media = TransportStream(4);
        var body = Join([encoder.HeaderChunk([]), encoder.SealChunk(ExchangeUploadChunks.Joinable, media)]);
        using var stopStall = new CancellationTokenSource();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = new FakeRelay(async (_, stream) =>
        {
            stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, stopStall.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            return [];
        });
        await using var healthy = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            await WriteChunkedAsync(stream, body, 7);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using var slowProxy = Proxy(slow, key);
        var slowPlay = PlayAsync(slowProxy);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            using var healthyProxy = Proxy(healthy, key);
            var (_, played) = await PlayAsync(healthyProxy).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(media, played);
            Assert.False(slowPlay.IsCompleted);
        }
        finally
        {
            slowProxy.Dispose();
            stopStall.Cancel();
            await slowPlay.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task ASealedRelay_GivesThePlayerClearMediaAndTheTrueType_AndTheServerSeesNone()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "video/mp2t");
        var key = encoder.NewGroupKey("own");
        var header = TransportStream(2);
        var media = new[] { TransportStream(20), TransportStream(11), TransportStream(7) };
        var body = Join([encoder.HeaderChunk(header), .. media.Select(chunk => encoder.SealChunk(ExchangeUploadChunks.Joinable, chunk))]);
        await using var relay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            // HTTP splits that cut records anywhere: 7-byte chunks.
            await WriteChunkedAsync(stream, body, 7);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using var proxy = Proxy(relay, key);
        string? failure = null;
        proxy.Failed += reason => failure = reason;

        var (head, played) = await PlayAsync(proxy);

        Assert.Null(failure);
        Assert.StartsWith("HTTP/1.0 200 OK", head);
        Assert.Contains("Content-Type: video/mp2t", head);
        Assert.DoesNotContain("x-fmsx-sealed", head);
        Assert.Equal(Join([header, .. media]), played);
        // What the relay carried: no plain media, and no MPEG-TS sync byte at every 188-byte step.
        var wire = Assert.Single(relay.Wire);
        Assert.True(wire.AsSpan().IndexOf(media[0].AsSpan(0, 64)) < 0, "the media bytes must not appear on the wire");
        var syncSteps = Enumerable.Range(0, wire.Length / 188).Count(step => wire[step * 188] == 0x47);
        Assert.True(syncSteps < 6, $"ciphertext must not keep the 0x47 rhythm ({syncSteps} of {wire.Length / 188})");
    }

    [Fact]
    public async Task ASealedAudioRelay_CarriesNoAdtsSyncAndPlaysAsAudio()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "audio/aac");
        var key = encoder.NewGroupKey("own");
        var frames = Join(Enumerable.Range(0, 40).Select(frame =>
        {
            var adts = new byte[200];
            adts[0] = 0xFF;
            adts[1] = 0xF1;
            for (var index = 2; index < adts.Length; index++)
            {
                adts[index] = (byte)(frame * 7 + index);
            }

            return adts;
        }));
        var body = Join([encoder.HeaderChunk([]), encoder.SealChunk(ExchangeUploadChunks.Joinable, frames)]);
        await using var relay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            await WriteChunkedAsync(stream, body, 1000);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using var proxy = Proxy(relay, key);

        var (head, played) = await PlayAsync(proxy);

        Assert.Contains("Content-Type: audio/aac", head);
        Assert.Equal(frames, played);
        var wire = Assert.Single(relay.Wire);
        var syncAtFrameSteps = Enumerable.Range(0, wire.Length / 200).Count(step => wire[step * 200] == 0xFF && wire[step * 200 + 1] == 0xF1);
        Assert.True(syncAtFrameSteps < 3, "ciphertext must not keep the ADTS sync rhythm");
    }

    [Fact]
    public async Task AWrongGroupKey_PlaysNothingAndSaysSealedFailed()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "audio/aac");
        encoder.NewGroupKey("own");
        var body = Join([encoder.HeaderChunk([]), encoder.SealChunk(ExchangeUploadChunks.Joinable, [1, 2, 3, 4])]);
        await using var relay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            await WriteChunkedAsync(stream, body, 64);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using var proxy = Proxy(relay, new byte[32]);
        string? failure = null;
        proxy.Failed += reason => failure = reason;

        var (head, played) = await PlayAsync(proxy);

        Assert.Empty(played);
        Assert.StartsWith("HTTP/1.0 502", head);
        await Until(() => failure is not null);
        Assert.Equal("sealed-failed", failure);
        Assert.Equal("sealed-failed", proxy.LastFailure);
    }

    [Fact]
    public async Task ATamperedRecord_StopsThePlayAndNothingAfterItIsPlayed()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "audio/aac");
        var key = encoder.NewGroupKey("own");
        var good = encoder.SealChunk(ExchangeUploadChunks.Joinable, [1, 1, 1, 1]);
        var bad = encoder.SealChunk(ExchangeUploadChunks.Joinable, [2, 2, 2, 2]);
        bad[^1] ^= 0x01;
        var after = encoder.SealChunk(ExchangeUploadChunks.Joinable, [3, 3, 3, 3]);
        var body = Join([encoder.HeaderChunk([]), good, bad, after]);
        await using var relay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            await WriteChunkedAsync(stream, body, 4096);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using var proxy = Proxy(relay, key);
        string? failure = null;
        proxy.Failed += reason => failure = reason;

        var (_, played) = await PlayAsync(proxy);

        Assert.Equal([1, 1, 1, 1], played);
        await Until(() => failure is not null);
        Assert.Equal("sealed-failed", failure);
    }

    [Fact]
    public async Task AnEpochTheListenerDoesNotHold_ReconnectsOnceAndReadsTheCurrentHeader()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "audio/aac");
        var key = encoder.NewGroupKey("own");
        var firstHeader = encoder.HeaderChunk([]);
        var firstMedia = encoder.SealChunk(ExchangeUploadChunks.Joinable, [1, 1, 1, 1]);
        encoder.NewEpoch();
        var secondHeader = encoder.HeaderChunk([]);
        var secondMedia = encoder.SealChunk(ExchangeUploadChunks.Joinable, [2, 2, 2, 2]);
        // Connection 1 jumps to epoch 2 without its ring (a replacement upload the listener missed); connection 2 reads it whole.
        var first = Join([firstHeader, firstMedia, secondMedia]);
        var second = Join([secondHeader, secondMedia]);
        await using var relay = new FakeRelay(async (number, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            var body = number == 1 ? first : second;
            await WriteChunkedAsync(stream, body, 4096);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using var proxy = Proxy(relay, key);
        string? failure = null;
        proxy.Failed += reason => failure = reason;

        var (head, played) = await PlayAsync(proxy);

        Assert.Null(failure);
        Assert.Contains("Content-Type: audio/aac", head);
        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2], played);
        Assert.Equal(2, relay.Connections);
    }

    [Fact]
    public async Task AnEpochStillNotHeldAfterTheReconnect_EndsWithSealedFailed()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "audio/aac");
        var key = encoder.NewGroupKey("own");
        var header = encoder.HeaderChunk([]);
        encoder.NewEpoch();
        var orphan = encoder.SealChunk(ExchangeUploadChunks.Joinable, [9, 9]);
        var body = Join([header, orphan]);
        await using var relay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            await WriteChunkedAsync(stream, body, 4096);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using var proxy = Proxy(relay, key);
        string? failure = null;
        proxy.Failed += reason => failure = reason;

        var (_, played) = await PlayAsync(proxy);

        Assert.Empty(played);
        await Until(() => failure is not null);
        Assert.Equal("sealed-failed", failure);
        Assert.Equal(2, relay.Connections);
    }

    // ------------------------------------------------------------------ the whole chain: producer -> server -> listener

    private static async IAsyncEnumerable<byte[]> Chunks(IEnumerable<byte[]> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    /// <summary>What a content-agnostic relay server does with an upload: keep the header chunk, forward every chunk's payload.</summary>
    private static async Task<(List<(byte Flags, byte[] Payload)> Chunks, byte[] Body)> ServerSideOfAsync(MemoryStream upload)
    {
        upload.Position = 0;
        var chunks = new List<(byte, byte[])>();
        try
        {
            while (await ExchangeUploadChunks.ReadAsync(upload, CancellationToken.None) is { } chunk)
            {
                chunks.Add(chunk);
            }
        }
        catch (EndOfStreamException)
        {
            // An upload that was retired just closes: there is no zero-length end that would end the broadcast.
        }

        return (chunks, Join(chunks.Select(chunk => chunk.Item2)));
    }

    [Theory]
    [InlineData("audio/aac")]
    [InlineData("video/mp2t")]
    public async Task ProducerToListener_TheServerSeesOnlyCiphertext_TheOwnDeviceHearsItAndAStrangerCannot(string mediaType)
    {
        var video = mediaType == "video/mp2t";
        using var phone = ExchangeE2eKeyPair.Generate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var sealer = new ExchangeBroadcastSealer("b1", mediaType, ExchangeTunnelCertificate.Create());
        var box = sealer.Seal("pc", self, [new ExchangeSealedRecipient("phone", phone.PublicPoint)]);
        var formatHeader = video ? TransportStream(2) : [];
        var media = video
            ? new[] { TransportStream(30), TransportStream(30) }
            : new[] { AdtsFrames(60), AdtsFrames(60) };

        // The producer's upload body, as the product writes it.
        using var upload = new MemoryStream();
        await ExchangeSealedUpload.WriteAsync(upload, sealer.Relay, formatHeader, Chunks(media), null, CancellationToken.None);
        var (chunks, body) = await ServerSideOfAsync(upload);

        // What the server received: a header chunk, then joinable chunks - and no plain media anywhere in them.
        Assert.Equal(ExchangeUploadChunks.Header, chunks[0].Flags);
        Assert.All(chunks.Skip(1), chunk => Assert.Equal(ExchangeUploadChunks.Joinable, chunk.Flags));
        foreach (var plain in media.Append(formatHeader).Where(item => item.Length > 0))
        {
            Assert.True(body.AsSpan().IndexOf(plain.AsSpan(0, 64)) < 0, "the server must not see plain media");
        }

        if (video)
        {
            Assert.True(Enumerable.Range(0, body.Length / 188).Count(step => body[step * 188] == 0x47) < body.Length / 188 / 4,
                "no MPEG-TS sync byte at 188-byte steps");
        }
        else
        {
            Assert.True(Enumerable.Range(0, body.Length / 200).Count(step => body[step * 200] == 0xFF && body[step * 200 + 1] == 0xF1) < 3,
                "no ADTS sync pattern");
        }

        // The listener side: the server relays the payloads chunked; the own device opens the box and plays.
        var ownKey = OpenRelayKey(box, "phone", phone, self);
        await using var relay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            await WriteChunkedAsync(stream, body, 1500);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return body;
        });
        using (var proxy = Proxy(relay, ownKey))
        {
            var (head, played) = await PlayAsync(proxy);
            Assert.Contains($"Content-Type: {mediaType}", head);
            Assert.Equal(Join([formatHeader, .. media]), played);
        }

        // Anyone else - a key the box was not addressed to - plays nothing.
        using var strangerProxy = Proxy(relay, new byte[32]);
        var (strangerHead, strangerPlayed) = await PlayAsync(strangerProxy);
        Assert.Empty(strangerPlayed);
        Assert.StartsWith("HTTP/1.0 502", strangerHead);
    }

    private static byte[] OpenRelayKey(ExchangeSealedBox box, string deviceId, ExchangeE2eKeyPair reader, ExchangeE2eKeyPair sender)
    {
        var opened = ExchangeSealedBoxes.Open(box, ExchangeSealedContext.Broadcast("b1"), deviceId, reader, sender.PublicPoint);
        Assert.True(opened.Opened);
        using var payload = System.Text.Json.JsonDocument.Parse(opened.Payload!);
        Assert.True(ExchangeBase64Url.TryDecode(payload.RootElement.GetProperty("relayKey").GetString(), out var key));
        return key;
    }

    private static byte[] AdtsFrames(int frames) => Join(Enumerable.Range(0, frames).Select(frame =>
    {
        var adts = new byte[200];
        adts[0] = 0xFF;
        adts[1] = 0xF1;
        for (var index = 2; index < adts.Length; index++)
        {
            adts[index] = (byte)(frame * 13 + index);
        }

        return adts;
    }));

    [Fact]
    public async Task ARetiredUploadEndsWithoutTheCleanEnd_AndAHeaderIsAlwaysFirst()
    {
        using var sealer = new ExchangeBroadcastSealer("b1", "audio/aac", ExchangeTunnelCertificate.Create());
        using var upload = new MemoryStream();
        using var retire = new CancellationTokenSource();
        var announced = false;

        async IAsyncEnumerable<byte[]> OneThenRetire()
        {
            yield return [1, 2, 3];
            retire.Cancel();
            await Task.Delay(50);
            yield return [4, 5, 6];
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExchangeSealedUpload.WriteAsync(upload, sealer.Relay, ReadOnlyMemory<byte>.Empty, OneThenRetire(), () => announced = true, retire.Token));

        Assert.True(announced);
        var (chunks, _) = await ServerSideOfAsync(upload);
        Assert.Equal(ExchangeUploadChunks.Header, chunks[0].Flags);
        Assert.True(chunks.Count >= 2);
    }

    // ------------------------------------------------------------------ latency (requirement 6)

    private static async Task<double> FirstBodyByteMillisecondsAsync(ExchangeRelayProxy proxy)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, proxy.BoundPort);
        var stream = player.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {new Uri(proxy.LoopbackUrl).AbsolutePath} HTTP/1.1\r\nHost: x\r\n\r\n"));
        var all = new List<byte>();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            all.AddRange(buffer.AsSpan(0, read).ToArray());
            var text = Encoding.ASCII.GetString([.. all]);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0 && all.Count > end + 4)
            {
                return clock.Elapsed.TotalMilliseconds;
            }
        }

        throw new InvalidOperationException("The relay ended before any media arrived.");
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    /// `expected: within item I | actual: ..` - sealing adds the cost of one AEAD pass at the producer and one at the listener
    /// to a chunk, and nothing else to the path. Measured on the loopback fan-out: the first media byte through the proxy,
    /// sealed and plain, and the per-chunk cost for a 128 kbit/s audio chunk and a 4 Mbit/s video keyframe chunk. Item I's
    /// first picture is due within the keyframe interval plus 1 s of connecting and steady state within 2 s, so the bound
    /// asserted here is far inside it: the layer must add under 100 ms to the first byte and under 20 ms to a chunk.
    /// </summary>
    [Fact]
    public async Task SealingKeepsTheFirstByteAndTheFanOutInsideTheBudget_MeasuredBeforeAndAfter()
    {
        var audio = AdtsFrames(40);
        using var encoder = new ExchangeSealedRelayEncoder("b1", "audio/aac");
        var key = encoder.NewGroupKey("own");
        var sealedBody = Join([encoder.HeaderChunk([]), encoder.SealChunk(ExchangeUploadChunks.Joinable, audio)]);
        await using var plainRelay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead("audio/aac")));
            await WriteChunkedAsync(stream, audio, 1500);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return audio;
        });
        await using var sealedRelay = new FakeRelay(async (_, stream) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RelayHead()));
            await WriteChunkedAsync(stream, sealedBody, 1500);
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            return sealedBody;
        });
        var plain = new List<double>();
        var sealedRuns = new List<double>();
        for (var run = 0; run < 25; run++)
        {
            using (var proxy = ExchangeRelayProxy.Start(RelayUrl, null, plainRelay.Connect))
            {
                plain.Add(await FirstBodyByteMillisecondsAsync(proxy));
            }

            using (var proxy = Proxy(sealedRelay, key))
            {
                sealedRuns.Add(await FirstBodyByteMillisecondsAsync(proxy));
            }
        }

        // Per-chunk cost at the producer (seal) and the listener (open), the two places the layer adds work.
        var audioChunk = AdtsFrames(40);
        var videoChunk = new byte[256 * 1024];
        Random.Shared.NextBytes(videoChunk);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < 200; index++)
        {
            _ = encoder.SealChunk(ExchangeUploadChunks.Joinable, audioChunk);
        }

        var audioSealMs = watch.Elapsed.TotalMilliseconds / 200;
        watch.Restart();
        for (var index = 0; index < 50; index++)
        {
            _ = encoder.SealChunk(ExchangeUploadChunks.Joinable, videoChunk);
        }

        var videoSealMs = watch.Elapsed.TotalMilliseconds / 50;
        var decoder = new ExchangeSealedRelayDecoder("b1", [key]);
        _ = decoder.Feed(encoder.HeaderChunk([]), new List<byte[]>());
        var videoRecord = encoder.SealChunk(ExchangeUploadChunks.Joinable, videoChunk);
        watch.Restart();
        for (var index = 0; index < 50; index++)
        {
            _ = decoder.Feed(videoRecord, new List<byte[]>());
        }

        var videoOpenMs = watch.Elapsed.TotalMilliseconds / 50;
        var line = $"first media byte through the proxy, median of 25: plain {Median(plain):F1} ms | sealed {Median(sealedRuns):F1} ms | " +
            $"seal 8 KiB audio chunk {audioSealMs:F3} ms | seal 256 KiB video chunk {videoSealMs:F3} ms | open 256 KiB video chunk {videoOpenMs:F3} ms";
        Console.WriteLine("SP-0226 LATENCY " + line);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sp-0226-latency.txt"), line);

        Assert.True(Median(sealedRuns) - Median(plain) < 100, line);
        Assert.True(audioSealMs < 20 && videoSealMs < 20 && videoOpenMs < 20, line);
    }
    // ------------------------------------------------------------------ the sealed tunnel

    /// <summary>The exchange side of one tunnel: `connect` answered `connected`, then TLS on the same stream when a certificate is given.</summary>
    private static async Task ServeTunnelAsync(TcpListener listener, ExchangeTunnelCertificate? inner, byte[] payload)
    {
        using var client = await listener.AcceptTcpClientAsync();
        Stream stream = client.GetStream();
        using var connect = await ExchangeProtocol.ReadAsync(stream, authenticated: false, CancellationToken.None);
        Assert.Equal("connect", ExchangeProtocol.String(connect.RootElement, "type"));
        await ExchangeProtocol.WriteAsync(stream, new { schemaVersion = 2, type = "connected" }, authenticated: false, CancellationToken.None);
        try
        {
            if (inner is not null)
            {
                stream = await inner.AcceptAsync(stream, CancellationToken.None);
            }

            var request = new byte[64];
            _ = await stream.ReadAsync(request);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            await Task.Delay(300);
        }
        catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException)
        {
            // The listener refused the leaf and hung up: the expected end of the wrong-pin case.
        }
    }

    [Fact]
    public async Task ASealedTunnel_RunsTlsInsideAndPlaysOnlyOnThePinnedLeaf()
    {
        using var inner = ExchangeTunnelCertificate.Create();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var payload = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/aac\r\n\r\nSEALED-TUNNEL-MEDIA");
            var serving = ServeTunnelAsync(listener, inner, payload);
            await using var forwarder = ExchangeTunnelForwarder.Start("fmsx://exchange.example.net:44022/b/b1/http",
                "http://192.0.2.1:8768/live-audio.aac", streamConnector: async (_, _, _, token) =>
                {
                    var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                    return tcp.GetStream();
                }, tunnelPin: inner.Pin);

            using var player = new TcpClient();
            await player.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
            var stream = player.GetStream();
            await stream.WriteAsync("GET /live-audio.aac HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray());
            var received = new byte[payload.Length];
            await stream.ReadExactlyAsync(received);

            Assert.Equal(payload, received);
            await serving;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ASealedTunnelOnAnotherLeaf_IsRefusedAsSealedFailedAndNothingIsPlayed()
    {
        using var inner = ExchangeTunnelCertificate.Create();
        using var stranger = ExchangeTunnelCertificate.Create();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serving = ServeTunnelAsync(listener, stranger, "LEAKED"u8.ToArray());
            await using var forwarder = ExchangeTunnelForwarder.Start("fmsx://exchange.example.net:44022/b/b1/http",
                "http://192.0.2.1:8768/live-audio.aac", streamConnector: async (_, _, _, token) =>
                {
                    var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                    return tcp.GetStream();
                }, tunnelPin: inner.Pin);
            string? refused = null;
            forwarder.Refused += reason => refused = reason;

            using var player = new TcpClient();
            await player.ConnectAsync(IPAddress.Loopback, forwarder.BoundPort);
            var stream = player.GetStream();
            var read = new byte[16];
            var count = 0;
            try
            {
                count = await stream.ReadAsync(read);
            }
            catch (IOException)
            {
                // A reset is the same refusal as a close.
            }

            Assert.Equal(0, count);
            await Until(() => refused is not null);
            Assert.Equal("sealed-failed", refused);
            Assert.Equal("sealed-failed", forwarder.LastRefusalReason);
            await serving;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void TheTunnelCertificate_IsAP256LeafWhosePinIsItsSha256()
    {
        using var inner = ExchangeTunnelCertificate.Create();

        Assert.StartsWith("SHA256:", inner.Pin);
        Assert.True(ExchangeTunnelTls.Matches(inner.Certificate, inner.Pin));
        Assert.False(ExchangeTunnelTls.Matches(inner.Certificate, "SHA256:" + new string('A', 43)));
        Assert.False(ExchangeTunnelTls.Matches(null, inner.Pin));
        Assert.Equal("ECDsa", inner.Certificate.PublicKey.Oid.FriendlyName is "ECC" ? "ECDsa" : inner.Certificate.PublicKey.Oid.FriendlyName);
        using var other = ExchangeTunnelCertificate.Create();
        Assert.NotEqual(inner.Pin, other.Pin);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected state was not reached.");
            }

            await Task.Delay(25);
        }
    }
}
