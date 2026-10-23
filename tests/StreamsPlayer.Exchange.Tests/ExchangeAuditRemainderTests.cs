using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

public sealed partial class ExchangeLoopRecoveryTests
{
    [Fact]
    public async Task DirectoryAbsenceDoesNotEraseTrust_AndARequestedRefreshWaitsForItsOwnAnswer()
    {
        using var certificate = DirectoryFixtureServer.NewCertificate();
        await using var server = new DirectoryFixtureServer(certificate);
        using var scope = new TestDirectory();
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        using var first = ExchangeE2eKeyPair.Generate();
        using var replacement = ExchangeE2eKeyPair.Generate();
        try
        {
            service.Start();
            using var follow = service.AcquireDirectory(true);
            await Until(() => server.Seen().Contains("list"));
            await server.SendAsync(KeyDirectory(1, first.Kem));
            await Until(() => service.Directory?.Revision == 1);
            Assert.Equal(first.PublicPoint, service.Trust.TrustedDevice("phone"));

            await server.SendAsync(new { schemaVersion = 2, type = "directory", revision = 2, devices = Array.Empty<object>(), broadcasts = Array.Empty<object>() });
            await Until(() => service.Directory?.Revision == 2);
            Assert.Equal(first.PublicPoint, service.Trust.TrustedDevice("phone"));

            using var ask = service.AcquireDirectory(false);
            var refreshed = service.WaitForDirectoryAsync(TimeSpan.FromSeconds(5));
            Assert.False(refreshed.IsCompleted);
            await server.SendAsync(KeyDirectory(3, replacement.Kem));
            Assert.True(await refreshed);
            Assert.Equal(first.PublicPoint, service.Trust.TrustedDevice("phone"));
            Assert.Equal(ExchangeSealedState.KeyChanged, service.StateOfDevice(Assert.Single(service.Directory!.Devices!)));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    private static object KeyDirectory(int revision, string kem) => new
    {
        schemaVersion = 2, type = "directory", revision,
        devices = new[] { new { deviceId = "phone", deviceName = "Phone", presence = "online", capabilities = new[] { "e2e-v1" }, e2eKey = new { v = 1, kem } } },
        broadcasts = Array.Empty<object>()
    };

    [Theory]
    [InlineData(false, 1025)]
    [InlineData(true, 65)]
    public async Task AnOutOfBoundsEnrollmentMakesNoConnectionAndErasesTheSecret(bool pairingCode, int length)
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) => await HoldOpen(tls, token));
        var service = new ExchangeSourceService(scope.Path);
        var secret = new string('p', length).ToCharArray();

        await service.EnrollAsync("localhost", server.Port, "test-login", pairingCode, secret, null);

        Assert.Equal("ExchangeInvalidEntry", service.EnrollmentOutcomeKey);
        Assert.Equal(0, server.Connections);
        Assert.All(secret, value => Assert.Equal('\0', value));
    }

    [Fact]
    public async Task ASourceWithNoReachableEndpointIsRefusedBeforeNativeCapture()
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);
        await producer.UpdateSettingsAsync(settings => settings with { Allowed = true, LanListening = false });

        Assert.False(await producer.StartAsync(BroadcastSource.Microphone));
        Assert.Equal("BroadcastNoReachableEndpoint", producer.OutcomeKey);
        Assert.Empty(producer.Running);
    }

    [Fact]
    public void BroadcasterRoleDoesNotDependOnTheCapturePermission()
    {
        using var scope = new TestDirectory();
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);

        Assert.False(producer.Settings.Allowed);
        Assert.Contains("broadcaster", exchange.Roles());
    }

    [Theory]
    [InlineData("RELAY")]
    [InlineData("HTTP")]
    public void PlaybackAndPreviewRouteRefuseMalformedSealedUrlsBeforeOpeningAProxy(string transport)
    {
        var endpoint = new FastMediaSorterBroadcastEndpoint("https://localhost/live", transport, "VIDEO_ONLY", null, null, null, null, true, 1000,
            Seal: new ExchangeEndpointSeal(1, ExchangeBase64Url.Encode(new byte[32])));
        string? failure = null;

        using var route = BroadcastVideoRoute.Open(endpoint, null, reason => failure = reason);

        Assert.Null(route);
        Assert.Equal("sealed-failed", failure);
    }

    [Fact]
    public void AProtectedLanVideoIsRefusedWithItsOwnReason()
    {
        var endpoint = new FastMediaSorterBroadcastEndpoint("http://192.168.1.2/live.ts", "HTTP", "VIDEO_ONLY", null, null, null, null, true, 1000);
        string? failure = null;

        using var route = BroadcastVideoRoute.Open(endpoint, new("u", "p"), reason => failure = reason, passwordProtected: true);

        Assert.Null(route);
        Assert.Equal(ExchangeBroadcastAuth.UnsupportedEndpointReason, failure);
    }

    [Fact]
    public async Task AConnectionStringFileIsRecognizedByContentRegardlessOfItsExtension()
    {
        using var scope = new TestDirectory();
        var path = Path.Combine(scope.Path, "server.txt");
        await File.WriteAllTextAsync(path, "FMSEXCH1:any-payload");
        Assert.True(ExchangeConnectionImport.RecognizesFile(path));
        await File.WriteAllTextAsync(path, "not a connection string");
        Assert.False(ExchangeConnectionImport.RecognizesFile(path));
    }

    [Fact]
    public async Task AnUntrustedDescriptorLeafCanBePresentedAndThenAcceptedOnlyByItsExactPin()
    {
        using var certificate = Certificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new System.Net.Security.SslStream(client.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
                        { ServerCertificate = certificate }, timeout.Token);
                    if (index == 1)
                    {
                        var request = new byte[4096];
                        Assert.True(await tls.ReadAsync(request, timeout.Token) > 0);
                        await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), timeout.Token);
                    }
                }
                catch (System.Security.Authentication.AuthenticationException) when (index == 0)
                {
                    // The first client only presents the untrusted leaf; it never accepts it.
                }
            }
        });
        try
        {
            var url = $"https://localhost:{port}/live-audio.aac";
            var leaf = await BroadcastCertificateTrust.ProbeAsync(url, timeout.Token);
            Assert.False(leaf!.PlatformTrusted);
            Assert.Equal(ExchangeProtocol.Fingerprint(certificate.RawData), leaf.Fingerprint);
            using var response = await new FastMediaSorterPlaybackTransport().OpenAsync(new Uri(url), null, leaf.Fingerprint, timeout.Token);
            Assert.Equal(200, response.StatusCode);
            Assert.NotNull(response.Stream);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ACastRefreshesItsDirectoryAndWritesTheBoxBeforeTheOffer_AnUnaddressedReceiverGetsNoOffer()
    {
        using var certificate = Certificate();
        using var sender = ExchangeE2eKeyPair.Generate();
        using var phone = ExchangeE2eKeyPair.Generate();
        using var scope = new TestDirectory();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var hello = await ExchangeProtocol.ReadAsync(tls, false, token);
            await ExchangeProtocol.WriteAsync(tls, new
            {
                schemaVersion = 2, type = "welcome", account = "test-login", accountKind = "member",
                publicEndpoint = "localhost:44022", serverVersion = "test", features = new[] { "directory", "sealed", "tunnel", "cast" }, keepaliveSeconds = 30
            }, false, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type == "list")
                {
                    await ExchangeProtocol.WriteAsync(tls, KeyDirectory(1, phone.Kem), true, token);
                }
                if (type == "broadcast-update")
                {
                    var box = ExchangeSealedBox.TryRead(frame.RootElement.GetProperty("broadcast").GetProperty("sealed"));
                    Assert.Contains(box!.Keys, key => key.To == "phone");
                }
            }
        });
        scope.Save(Account(server.Port, certificate).WithE2eKey(sender));
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);
        using var sealing = new BroadcastSealing("audit-cast", "audio/aac");
        var plan = new BroadcastWirePlan("audit-cast", exchange.Account.DeviceId, "t", BroadcastSource.Microphone,
            "192.168.1.2", 8768, null, "localhost:44022", false, true, null, 44100, 1, 128, Seal: BroadcastSealMode.Record);
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Microphone, BroadcastId = "audit-cast", Title = "t", StartedAt = DateTimeOffset.UtcNow,
            Encoder = null!, Capture = null!, Frames = new AdtsFrameStream(), Plan = plan, DescriptorJson = BroadcastDescriptorWriter.Write(plan),
            ExchangeStarted = true, Sealing = sealing
        };
        var states = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(producer)!;
        try
        {
            exchange.Start();
            await Until(() => seen.Contains("keepalive"));
            states[BroadcastSource.Microphone] = state;
            state.ExchangeStarted = true;
            var receiver = new ExchangeDeviceRecord("phone", "Phone", true,
                [new ExchangePlayPair("AUDIO_ONLY", "TUNNEL")], phone.PublicPoint, true);
            Assert.True(await producer.CastAsync(BroadcastSource.Microphone, receiver));
            await Until(() => seen.Contains("cast"));
            var frames = seen.ToArray();
            Assert.True(Array.LastIndexOf(frames, "broadcast-update") < Array.IndexOf(frames, "cast"));
            Assert.Contains("list", frames);
            Assert.False(await producer.CastAsync(BroadcastSource.Microphone, receiver with { DeviceId = "not-listed" }));
            Assert.Single(seen, type => type == "cast");
        }
        finally
        {
            states.Clear();
            await exchange.StopAsync();
        }
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ReestablishingTheProducerRotatesTheEpochForADeviceRemovedWhileOffline()
    {
        using var certificate = Certificate();
        using var sender = ExchangeE2eKeyPair.Generate();
        using var departed = ExchangeE2eKeyPair.Generate();
        using var phone = ExchangeE2eKeyPair.Generate();
        using var scope = new TestDirectory();
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var hello = await ExchangeProtocol.ReadAsync(tls, false, token);
            await ExchangeProtocol.WriteAsync(tls, new
            {
                schemaVersion = 2, type = "welcome", account = "test-login", accountKind = "member",
                publicEndpoint = "localhost:44022", serverVersion = "test", features = new[] { "directory", "sealed", "tunnel" }, keepaliveSeconds = 30
            }, false, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                switch (ExchangeProtocol.String(frame.RootElement, "type"))
                {
                    case "keepalive": online.TrySetResult(); break;
                    case "list": await ExchangeProtocol.WriteAsync(tls, KeyDirectory(1, phone.Kem), true, token); break;
                    case "broadcast-start":
                        await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "broadcast-started", broadcastId = "audit-reestablish" }, true, token);
                        break;
                }
            }
        });
        scope.Save(Account(server.Port, certificate).WithE2eKey(sender));
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);
        using var sealing = new BroadcastSealing("audit-reestablish", "audio/aac");
        var oldBox = sealing.Sealer.Seal(exchange.Account.DeviceId, sender,
            [new("departed", departed.PublicPoint), new("phone", phone.PublicPoint)]);
        var old = ExchangeSealedBoxes.Open(oldBox, "broadcast:audit-reestablish", "departed", departed, sender.PublicPoint);
        using var oldPayload = JsonDocument.Parse(old.Payload!);
        Assert.True(ExchangeBase64Url.TryDecode(oldPayload.RootElement.GetProperty("relayKey").GetString(), out var oldKey));
        var oldEpoch = sealing.Sealer.Relay.Epoch;
        // Only the wire state is exercised. These native capture owners are never started or disposed by the test.
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Microphone, BroadcastId = "audit-reestablish", Title = "t", StartedAt = DateTimeOffset.UtcNow,
            Encoder = null!, Capture = null!, Frames = new AdtsFrameStream(), Sealing = sealing, LanAddress = "192.168.1.2", LocalPort = 8768
        };
        try
        {
            exchange.Start();
            await online.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var establish = (Task)typeof(BroadcastProducerService).GetMethod("EstablishOnExchangeAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(producer, [state])!;
            await establish.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(state.ExchangeStarted);
            Assert.True(sealing.Sealer.Relay.Epoch > oldEpoch);
            Assert.DoesNotContain(sealing.Sealer.Box!.Keys, key => key.To == "departed");
            var decoder = new ExchangeSealedRelayDecoder("audit-reestablish", [oldKey]);
            var clear = new List<byte[]>();
            var stream = sealing.Sealer.Relay.HeaderChunk([]).Concat(sealing.Sealer.Relay.SealChunk(ExchangeUploadChunks.Joinable, [1, 2])).ToArray();
            Assert.Equal(ExchangeSealedRelayStatus.EpochNotHeld, decoder.Feed(stream, clear));
            Assert.Empty(clear);
        }
        finally
        {
            await exchange.StopAsync();
        }
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task AClosedSessionLogsOnlyTheLastEnvelopeType()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            using var keepalive = await ExchangeProtocol.ReadAsync(tls, true, token);
        });
        var account = Account(server.Port, certificate);
        scope.Save(account);
        var exchange = new ExchangeSourceService(scope.Path, log.Enqueue);
        try
        {
            exchange.Start();
            await Until(() => log.Any(line => line.Contains("envelope=keepalive", StringComparison.Ordinal)));
            Assert.DoesNotContain(log, line => line.Contains(account.Token!, StringComparison.Ordinal) || line.Contains(account.Login, StringComparison.Ordinal));
        }
        finally
        {
            await exchange.StopAsync();
        }
    }
}
