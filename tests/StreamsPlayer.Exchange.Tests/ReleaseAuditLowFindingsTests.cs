using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed partial class ExchangeLoopRecoveryTests
{
    [Fact]
    public async Task CancellingAQueuedPasswordChangeKeepsItsArraysUntilWrittenAndKeepsTheLateToken()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        scope.Save(Account(44022, certificate));
        var service = new ExchangeSourceService(scope.Path);
        var queue = Channel.CreateBounded<(object Envelope, TaskCompletionSource<bool> Written)>(1);
        typeof(ExchangeSourceService).GetField("_producerOutgoing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, queue);
        using var cancellation = new CancellationTokenSource();
        var oldPassword = "original-password".ToCharArray();
        var newPassword = "replacement-password".ToCharArray();

        var change = service.ChangePasswordAsync(oldPassword, newPassword, cancellation.Token);
        cancellation.Cancel();
        Assert.False(change.IsCompleted);
        var posted = await queue.Reader.ReadAsync();
        Assert.IsType<ExchangePasswordChange>(posted.Envelope);
        var envelope = JsonSerializer.SerializeToElement(posted.Envelope, ExchangeProtocol.JsonOptions);
        Assert.Equal("original-password", envelope.GetProperty("oldPassword").GetString());
        Assert.Equal("replacement-password", envelope.GetProperty("newPassword").GetString());
        posted.Written.SetResult(true);
        Assert.Equal("ExchangeUnanswered", (await change).Key);
        Assert.All(oldPassword.Concat(newPassword), character => Assert.Equal('\0', character));

        var answer = JsonSerializer.SerializeToElement(new { deviceToken = new string('z', 43) });
        typeof(ExchangeSourceService).GetMethod("HandlePasswordChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [answer]);
        Assert.Equal(new string('z', 43), service.Account.Token);
        await service.StopAsync();
    }

    [Fact]
    public async Task CancellingAfterEnrollmentKeepsTheCommittedMemberTokenAndKey()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var enrollment = await ExchangeProtocol.ReadAsync(tls, false, token);
            await EnrolledAs(tls, ["directory"], token);
            await HoldOpen(tls, token);
        });
        var previous = Account(server.Port, certificate) with { Guest = true };
        scope.Save(previous);
        var service = new ExchangeSourceService(scope.Path)
        {
            BeforeLeaveAsync = () => { cancellation.Cancel(); return Task.CompletedTask; }
        };
        try
        {
            await service.EnrollAsync("localhost", server.Port, "other-login", false, "Password1".ToCharArray(),
                ExchangeProtocol.Fingerprint(certificate.RawData), previous, cancellation.Token);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal("other-login", service.Account.Login);
            Assert.Equal(new string('k', 43), service.Account.Token);
            using var key = service.Account.LoadE2eKey();
            Assert.NotNull(key);
            Assert.Single(service.Guests.Entries);
        }
        finally { await service.StopAsync(); }
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task AReestablishFaultDoesNotSkipTheNextSource_AndAnOfflineAnswerCannotMarkItStarted()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        using var self = ExchangeE2eKeyPair.Generate();
        using var phone = ExchangeE2eKeyPair.Generate();
        BroadcastProducerService? producer = null;
        var staleAnswer = false;
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var hello = await ExchangeProtocol.ReadAsync(tls, false, token);
            await ExchangeProtocol.WriteAsync(tls, new
            {
                schemaVersion = 2, type = "welcome", account = "test-login", accountKind = "member",
                publicEndpoint = "localhost:44022", serverVersion = "test",
                features = new[] { "directory", "sealed", "tunnel" }, keepaliveSeconds = 30
            }, false, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                switch (ExchangeProtocol.String(frame.RootElement, "type"))
                {
                    case "list": await ExchangeProtocol.WriteAsync(tls, KeyDirectory(1, phone.Kem), true, token); break;
                    case "broadcast-start":
                        if (staleAnswer)
                            typeof(BroadcastProducerService).GetMethod("OnSessionOffline", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(producer, null);
                        await ExchangeProtocol.WriteAsync(tls, new
                        {
                            schemaVersion = 2, type = "broadcast-started",
                            broadcastId = frame.RootElement.GetProperty("broadcast").GetProperty("broadcastId").GetString()
                        }, true, token);
                        break;
                }
            }
        });
        scope.Save(Account(server.Port, certificate).WithE2eKey(self));
        var exchange = new ExchangeSourceService(scope.Path);
        producer = new BroadcastProducerService(scope.Path, exchange);
        var disposed = new BroadcastSealing("faulted-source", "audio/aac");
        disposed.Dispose();
        var bad = new BroadcastRunningSource
        {
            Source = BroadcastSource.Microphone, BroadcastId = "faulted-source", Title = "bad",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream(), Sealing = disposed
        };
        var good = new BroadcastRunningSource
        {
            Source = BroadcastSource.SystemSound, BroadcastId = "good-source", Title = "good",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream(), LanAddress = "192.168.1.2", LocalPort = 8768
        };
        var running = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        try
        {
            exchange.Start();
            await Until(() => exchange.StatusKey == "ExchangeOnline");
            running.Add(bad.Source, bad);
            running.Add(good.Source, good);
            var establish = typeof(BroadcastProducerService).GetMethod("EstablishRunningAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)establish.Invoke(producer, null)!).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(good.ExchangeStarted);

            running.Remove(bad.Source);
            good.ExchangeStarted = false;
            staleAnswer = true;
            await ((Task)establish.Invoke(producer, null)!).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(good.ExchangeStarted);
        }
        finally
        {
            running.Clear();
            bad.Sealing?.DirectoryLease?.Dispose();
            good.Sealing?.Dispose();
            producer.Dispose();
            await exchange.StopAsync();
        }
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public void GoingOfflineRetiresTheUploadBeforeAReconnectCanReplaceIt()
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);
        using var retirement = new CancellationTokenSource();
        var chunks = Channel.CreateBounded<BroadcastMediaChunk>(1);
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Microphone, BroadcastId = "offline-upload", Title = "fixture",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream(),
            UploadRetire = retirement, UploadChunks = chunks, ExchangeStarted = true
        };
        var running = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        running.Add(state.Source, state);
        try
        {
            typeof(BroadcastProducerService).GetMethod("OnSessionOffline", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(producer, null);
            Assert.True(retirement.IsCancellationRequested);
            Assert.False(state.ExchangeStarted);
            Assert.Null(state.UploadChunks);
            Assert.True(chunks.Reader.Completion.IsCompletedSuccessfully);
        }
        finally { running.Clear(); }
    }

    [Fact]
    public void ATrustWriteFailureBacksOffAndAnExplicitForgetStillPersists()
    {
        using var scope = new TestDirectory();
        var path = Path.Combine(scope.Path, "exchange-trust.bin");
        Directory.CreateDirectory(path); // A directory at the file path makes the final move fail.
        var book = new ExchangeTrustBook(scope.Path);
        using var key = ExchangeE2eKeyPair.Generate();
        book.For("entry").ObserveDevice("device", key.PublicPoint);
        book.SaveIfDirty();
        Directory.Delete(path);

        book.SaveIfDirty();
        Assert.False(File.Exists(path));
        Assert.Equal(key.PublicPoint, book.For("entry").TrustedDevice("device"));
        book.Forget("entry");
        Assert.True(File.Exists(path));
        Assert.Empty(new ExchangeTrustBook(scope.Path).For("entry").DeviceIds);
    }

    [Fact]
    public void ScreenUploadQueue_BuffersUntilHandshakeAcceptance_AndStartsAtKeyframe()
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        var lines = new List<string>();
        using var producer = new BroadcastProducerService(scope.Path, exchange, diagnostic: lines.Add);
        var chunks = Channel.CreateBounded<BroadcastMediaChunk>(4);
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Screen, BroadcastId = "screen-upload", Title = "screen",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream(),
            UploadChunks = chunks, ExchangeStarted = true
        };
        var running = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        running.Add(state.Source, state);
        var post = typeof(BroadcastProducerService).GetMethod("PostUploadChunk", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(BroadcastRunningSource), typeof(BroadcastMediaChunk)])!;

        // Before handshake accepts (UploadAccepted = false):
        // 1. Send first GOP (keyframe + 2 P-frames)
        post.Invoke(producer, [state, new BroadcastMediaChunk([1], true)]);
        post.Invoke(producer, [state, new BroadcastMediaChunk([2], false)]);
        post.Invoke(producer, [state, new BroadcastMediaChunk([3], false)]);
        // 2. Send second GOP (keyframe + 2 P-frames) - should replace the first GOP so memory stays bounded
        post.Invoke(producer, [state, new BroadcastMediaChunk([10], true)]);
        post.Invoke(producer, [state, new BroadcastMediaChunk([11], false)]);
        post.Invoke(producer, [state, new BroadcastMediaChunk([12], false)]);

        // Chunks channel should have 0 items yet, no loss logged
        Assert.DoesNotContain(lines, l => l.Contains("code=queue-full"));
        Assert.NotNull(state.UploadChunks);
        Assert.Equal(3, state.UploadStartupBuffer.Count);
        Assert.Equal(10, state.UploadStartupBuffer[0].Payload[0]);

        // Handshake completes: simulate relay-upload-accepted
        lock (state.UploadGate)
        {
            state.UploadAccepted = true;
            if (state.UploadStartupBuffer.Count > 0)
            {
                state.UploadJoined = true;
                foreach (var buffered in state.UploadStartupBuffer)
                    chunks.Writer.TryWrite(buffered);
                state.UploadStartupBuffer.Clear();
            }
        }

        // Chunks channel now has 3 items: [10], [11], [12]
        Assert.True(chunks.Reader.TryRead(out var read1));
        Assert.Equal(10, read1!.Payload[0]);
        Assert.True(read1.Joinable);
        Assert.True(chunks.Reader.TryRead(out var read2));
        Assert.Equal(11, read2!.Payload[0]);
        Assert.False(read2.Joinable);
        Assert.True(chunks.Reader.TryRead(out var read3));
        Assert.Equal(12, read3!.Payload[0]);
        Assert.False(read3.Joinable);
        Assert.False(chunks.Reader.TryRead(out _));

        // Now in steady state: posting writes directly to chunks
        post.Invoke(producer, [state, new BroadcastMediaChunk([20], false)]);
        Assert.True(chunks.Reader.TryRead(out var read4));
        Assert.Equal(20, read4!.Payload[0]);

        // In steady state: filling the queue causes queue-full loss
        for (var i = 0; i < 5; i++)
            post.Invoke(producer, [state, new BroadcastMediaChunk([(byte)(30 + i)], false)]);

        Assert.Contains(lines, l => l.Contains("code=queue-full"));
        Assert.Null(state.UploadChunks);
    }

    [Fact]
    public void AudioUploadQueue_BuffersUntilHandshakeAcceptance()
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        var lines = new List<string>();
        using var producer = new BroadcastProducerService(scope.Path, exchange, diagnostic: lines.Add);
        var chunks = Channel.CreateBounded<BroadcastMediaChunk>(4);
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Microphone, BroadcastId = "audio-upload", Title = "mic",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream(),
            UploadChunks = chunks, ExchangeStarted = true
        };
        var running = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        running.Add(state.Source, state);
        var post = typeof(BroadcastProducerService).GetMethod("PostUploadChunk", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(BroadcastRunningSource), typeof(byte[])])!;

        // Before handshake accepts: send 3 audio chunks
        post.Invoke(producer, [state, new byte[] { 1, 2 }]);
        post.Invoke(producer, [state, new byte[] { 3, 4 }]);
        post.Invoke(producer, [state, new byte[] { 5, 6 }]);

        Assert.DoesNotContain(lines, l => l.Contains("code=queue-full"));
        Assert.NotNull(state.UploadChunks);
        Assert.Equal(3, state.UploadStartupBuffer.Count);

        // Handshake completes
        lock (state.UploadGate)
        {
            state.UploadAccepted = true;
            if (state.UploadStartupBuffer.Count > 0)
            {
                state.UploadJoined = true;
                foreach (var buffered in state.UploadStartupBuffer)
                    chunks.Writer.TryWrite(buffered);
                state.UploadStartupBuffer.Clear();
            }
        }

        Assert.True(chunks.Reader.TryRead(out var read1));
        Assert.Equal(1, read1!.Payload[0]);
        Assert.True(chunks.Reader.TryRead(out var read2));
        Assert.Equal(3, read2!.Payload[0]);
        Assert.True(chunks.Reader.TryRead(out var read3));
        Assert.Equal(5, read3!.Payload[0]);
        Assert.False(chunks.Reader.TryRead(out _));
    }
}
