using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed partial class ExchangeLoopRecoveryTests
{
    [Fact]
    public async Task ProducerDiagnostics_PendingStart_IsVisibleAndDoesNotQueueADuplicate()
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        var lines = new List<string>();
        using var producer = new BroadcastProducerService(scope.Path, exchange, diagnostic: lines.Add);
        await producer.UpdateSettingsAsync(settings => settings with { Allowed = true, LanListening = false });
        var operations = (SemaphoreSlim)typeof(BroadcastProducerService)
            .GetField("_sourceOperations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        await operations.WaitAsync();
        var pending = producer.StartAsync(BroadcastSource.Screen);
        try
        {
            Assert.True(producer.IsStarting(BroadcastSource.Screen));
            Assert.Equal(BroadcastProducerState.Starting, Assert.Single(producer.StartVerdicts).State);
            Assert.False(await producer.StartAsync(BroadcastSource.Screen));
        }
        finally { operations.Release(); }
        Assert.False(await pending);
        Assert.False(producer.IsStarting(BroadcastSource.Screen));
        Assert.Single(lines, line => line.Contains("step=start-asked"));
    }

    [Fact]
    public async Task ProducerDiagnostics_ListenerCounts_LogOnlyChanges()
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        var lines = new List<string>();
        using var producer = new BroadcastProducerService(scope.Path, exchange, diagnostic: lines.Add);
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Screen, BroadcastId = "counts", Title = "screen",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream()
        };
        var running = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        running.Add(state.Source, state);
        var changed = typeof(BroadcastProducerService).GetMethod("OnLanListenerCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var count in new[] { 0, 1, 1, 1, 0, 0 }) changed.Invoke(producer, [state, count]);
        var counts = lines.Where(line => line.Contains("step=listeners-changed")).ToArray();
        Assert.Equal(2, counts.Length);
        Assert.EndsWith("lan=1", counts[0]);
        Assert.EndsWith("lan=0", counts[1]);
        await producer.StopAllAsync();
    }

    [Theory]
    [InlineData(BroadcastSource.Microphone)]
    [InlineData(BroadcastSource.SystemSound)]
    [InlineData(BroadcastSource.Screen)]
    [InlineData(BroadcastSource.Camera)]
    public async Task ProducerDiagnostics_OfflineWithoutLan_RefusesBeforeCaptureAndRecordsFacts(BroadcastSource source)
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        var lines = new List<string>();
        using var producer = new BroadcastProducerService(scope.Path, exchange, diagnostic: lines.Add);
        await producer.UpdateSettingsAsync(settings => settings with { Allowed = true, LanListening = false });

        Assert.False(await producer.StartAsync(source));
        Assert.Empty(producer.Running);
        Assert.False(producer.IsStarting(source));
        var verdict = Assert.Single(producer.StartVerdicts);
        Assert.Equal(BroadcastProducerState.Failed, verdict.State);
        Assert.Equal("BroadcastNoReachableEndpoint", verdict.ReasonKey);
        Assert.Single(lines, line => line.Contains("step=start-asked"));
        Assert.Single(lines, line => line.Contains("step=start-refused"));
        var facts = Assert.Single(lines, line => line.Contains("step=reachability"));
        foreach (var fact in new[] { "reachable=False", "lan-enabled=False", "lan-address=none", "session-online=False",
                     "public-endpoint=none", "guest=False", "sealed=False", "relay=False", "tunnel=False", "own-key=False" })
            Assert.Contains(fact, facts);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, "capacity")]
    [InlineData(true, "timeout")]
    public async Task ProducerDiagnostics_ServerVerdict_UsesWrittenBoxAndNeverLeaksSecrets(bool recipient, string? refusal)
    {
        using var scope = new TestDirectory();
        using var certificate = Certificate();
        using var self = ExchangeE2eKeyPair.Generate();
        using var phone = ExchangeE2eKeyPair.Generate();
        var lines = new ConcurrentQueue<string>();
        var seen = new ConcurrentQueue<string>();
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
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type == "list")
                {
                    var directory = recipient ? KeyDirectory(1, phone.Kem) : new
                    {
                        schemaVersion = 2, type = "directory", revision = 1,
                        devices = Array.Empty<object>(), broadcasts = Array.Empty<object>()
                    } as object;
                    await ExchangeProtocol.WriteAsync(tls, directory!, true, token);
                }
                if (type == "broadcast-start" && refusal != "timeout")
                {
                    var id = frame.RootElement.GetProperty("broadcast").GetProperty("broadcastId").GetString();
                    object answer = refusal is null
                        ? new { schemaVersion = 2, type = "broadcast-started", broadcastId = id }
                        : new { schemaVersion = 2, type = "refused", reason = refusal, broadcastId = id };
                    await ExchangeProtocol.WriteAsync(tls, answer, true, token);
                }
            }
        });
        var account = Account(server.Port, certificate).WithE2eKey(self);
        scope.Save(account);
        var exchange = new ExchangeSourceService(scope.Path);
        using var log = new CurrentLog(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange, diagnostic: line => { lines.Enqueue(line); log.Event(line); });
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Screen, BroadcastId = "diag-test", Title = "screen",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream(), LocalPort = 8769,
            LanAddress = "192.168.1.2", OpaqueToken = "0123456789abcdef0123456789abcdef", LinkPair = new("listener", "synthetic-link-password")
        };
        var running = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        try
        {
            exchange.Start();
            await Until(() => exchange.StatusKey == "ExchangeOnline");
            running.Add(state.Source, state);
            await ((Task)typeof(BroadcastProducerService).GetMethod("EstablishOnExchangeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(producer, [state])!).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains(lines, line => line.Contains("step=directory-awaited"));
            Assert.Contains(lines, line => line.Contains("step=record-sealed | recipients=" + (recipient ? 1 : 0)));
            Assert.Contains(lines, line => line.Contains("step=broadcast-start | sent=True"));
            Assert.Equal(refusal is null, state.ExchangeStarted);
            var secrets = new List<string> { account.Token!, account.Pin!, account.E2ePrivateKey!, state.OwnPair.Password,
                "synthetic-link-password", state.OpaqueToken!, state.Sealing!.Sealer.Tunnel.Pin };
            var sealKey = state.Sealing.Sealer.Relay.GroupKey("own")!;
            secrets.Add(ExchangeBase64Url.Encode(sealKey));
            secrets.Add(Convert.ToBase64String(sealKey));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(sealKey);
            if (refusal is null)
            {
                Assert.Contains(lines, line => line.Contains("step=broadcast-start-accepted"));
                Assert.Equal(recipient ? 1 : 0, state.RecipientNames.Count);
                Assert.Equal("localhost:" + server.Port, state.AnnouncedServer);
                await producer.StopAsync(state.Source);
                Assert.Single(lines, line => line.Contains("step=stop | cause=person"));
                Assert.Single(lines, line => line.Contains("step=broadcast-end | sent=True"));
                Assert.Equal(BroadcastProducerState.Stopped, Assert.Single(producer.StartVerdicts).State);
            }
            else
            {
                Assert.Contains(lines, line => line.Contains(refusal == "timeout" ? "step=broadcast-start-timed-out" : "step=broadcast-start-refused"));
                Assert.NotNull(state.FailureKey);
            }
            var text = string.Join('\n', lines);
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("verifiers", text);
            Assert.DoesNotContain("descriptor", text);

            var archivePath = DiagnosticArchiveBuilder.Build(scope.Path, Path.Combine(scope.Path, "report"), "test", DateTimeOffset.UtcNow);
            using var archive = ZipFile.OpenRead(archivePath);
            using var reader = new StreamReader(archive.GetEntry(DiagnosticLogFiles.CurrentLogName)!.Open());
            Assert.Contains("[Diag] BROADCAST", await reader.ReadToEndAsync());
        }
        finally
        {
            await producer.StopAllAsync();
            await exchange.StopAsync();
        }
        server.ThrowIfScriptFailed();
    }

    [Theory]
    [InlineData((int)BroadcastStopCause.SignOut, "sign-out")]
    [InlineData((int)BroadcastStopCause.ApplicationExit, "application-exit")]
    [InlineData((int)BroadcastStopCause.CaptureLoss, "capture-loss")]
    public async Task ProducerDiagnostics_StopCause_IsExplicitAndRecordedOnce(int cause, string expected)
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        var lines = new List<string>();
        using var producer = new BroadcastProducerService(scope.Path, exchange, diagnostic: lines.Add);
        var running = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(producer)!;
        running.Add(BroadcastSource.Screen, new()
        {
            Source = BroadcastSource.Screen, BroadcastId = "stop-test", Title = "screen",
            StartedAt = DateTimeOffset.UtcNow, Frames = new AdtsFrameStream()
        });
        await producer.StopAsync(BroadcastSource.Screen, (BroadcastStopCause)cause);
        await producer.StopAsync(BroadcastSource.Screen, (BroadcastStopCause)cause);
        Assert.Single(lines, line => line.Contains("step=stop | cause=" + expected));
        Assert.Empty(producer.Running);
    }
}
