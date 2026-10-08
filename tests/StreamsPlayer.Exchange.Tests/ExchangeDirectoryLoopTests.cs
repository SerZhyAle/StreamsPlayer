using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0201 requirement 1, over the real service loop: after <c>welcome</c> the service sends <c>list</c>,
/// subscribes once the answer arrives, applies <c>changed</c> pushes, answers a revision gap with a new
/// <c>list</c>, and hides this device's own records from every snapshot it publishes.
/// </summary>
public sealed class ExchangeDirectoryLoopTests
{
    [Fact]
    public async Task ListSubscribeChangedAndGapFollowTheDirectoryRules()
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        string? ownDeviceId = null;
        var observed = new ConcurrentQueue<ExchangeDirectorySnapshot>();
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43),
                    ExchangeProtocol.Fingerprint(certificate.RawData), true));
                service = new ExchangeSourceService(directory);
                ownDeviceId = service.Account.DeviceId;
                service.DirectoryChanged += () =>
                {
                    if (service.Directory is { } snapshot)
                    {
                        observed.Enqueue(snapshot);
                    }
                };
                service.Start();

                await Until(() => observed.Any(snapshot => snapshot.LiveBroadcastIds.Contains("b1")));
                // Requirement 2: this device's own record never reaches the view it is shown.
                Assert.DoesNotContain(observed, snapshot => BroadcastIds(snapshot).Contains("b0"));
                Assert.Contains(observed, snapshot =>
                    snapshot.Groups.Any(group => group.DeviceName == "Pixel 8" && group.IsOnline));

                await Until(() => observed.Any(snapshot => snapshot.LiveBroadcastIds.Count == 0),
                    $"status={service.StatusKey} observed=[{string.Join("|", observed.Select(s => $"{s.Revision}:{s.LiveBroadcastIds.Count}:{s.Groups.Count}"))}]");
                await Until(() => observed.Any(snapshot => snapshot.LiveBroadcastIds.Contains("b2")));
                var last = observed.Last();
                Assert.Contains("b2", last.LiveBroadcastIds);
                Assert.DoesNotContain("b1", last.LiveBroadcastIds);
            }, async (tls, token) =>
            {
                using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
                Assert.Equal("hello", ExchangeProtocol.String(opening.RootElement, "type"));
                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2, type = "welcome", account = "test-login",
                    publicEndpoint = "localhost:44022", serverVersion = "test",
                    features = new[] { "directory" }, keepaliveSeconds = 30
                }, false, token);

                await ExchangeProtocol.WriteAsync(tls, DirectoryJson(1, ownDeviceId, withB1: true), true, token);
                await ExpectAsync(tls, "subscribe", token);
                // The record left: a removal push, one revision later.
                await ExchangeProtocol.WriteAsync(tls, ChangeJson(2, removal: true), true, token);
                // A gap: revision jumps, the only honest answer is a new list.
                await ExchangeProtocol.WriteAsync(tls, ChangeJson(5, removal: false), true, token);
                await ExpectAsync(tls, "list", token);
                await ExchangeProtocol.WriteAsync(tls, DirectoryJson(5, ownDeviceId, withB1: false), true, token);
                await Task.Delay(Timeout.Infinite, token);
            });
        }
        finally
        {
            if (service is not null)
            {
                await service.StopAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition, string? context = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            try
            {
                await Task.Delay(20, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException($"Timed out: {context}");
            }
        }
    }

    private static IEnumerable<string> BroadcastIds(ExchangeDirectorySnapshot snapshot) =>
        snapshot.Groups.SelectMany(group => group.Broadcasts).Select(view => view.Record.BroadcastId);

    private static async Task ExpectAsync(SslStream tls, string type, CancellationToken token)
    {
        while (true)
        {
            using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
            if (ExchangeProtocol.String(frame.RootElement, "type") == type)
            {
                return;
            }
        }
    }

    private static object DirectoryJson(long revision, string? ownDeviceId, bool withB1) => new
    {
        schemaVersion = 2,
        type = "directory",
        revision,
        devices = new object[]
        {
            new { deviceId = "phone", deviceName = "Pixel 8", presence = "online" },
            new { deviceId = ownDeviceId, deviceName = "This PC", presence = "online" }
        },
        broadcasts = withB1
            ? new object[]
            {
                new
                {
                    broadcastId = "b1", deviceId = "phone", title = "Kitchen", mode = "AUDIO_ONLY",
                    descriptor = Descriptor("http://192.168.1.97:8768/live-audio.aac")
                },
                new
                {
                    broadcastId = "b0", deviceId = ownDeviceId, title = "Mine", mode = "AUDIO_ONLY",
                    descriptor = Descriptor("http://192.168.1.98:8768/live-audio.aac")
                }
            }
            : new object[]
            {
                new
                {
                    broadcastId = "b2", deviceId = "phone", title = "Kitchen again", mode = "AUDIO_ONLY",
                    descriptor = Descriptor("http://192.168.1.97:9000/live-audio.aac")
                }
            }
    };

    private static object ChangeJson(long revision, bool removal) => new
    {
        schemaVersion = 2,
        type = "changed",
        revision,
        upserts = new { devices = Array.Empty<object>(), broadcasts = Array.Empty<object>() },
        removals = removal
            ? new { deviceIds = Array.Empty<string>(), broadcastIds = new[] { "b1" } }
            : new { deviceIds = Array.Empty<string>(), broadcastIds = Array.Empty<string>() }
    };

    private static object Descriptor(string url) => new
    {
        schemaVersion = 1,
        url,
        title = "Kitchen",
        mode = "AUDIO_ONLY",
        sourceId = "AAECAwQFBgcICQoLDA0ODw",
        isLive = true,
        endpoints = new object[] { new { url, transport = "HTTP", mode = "AUDIO_ONLY", isLive = true } }
    };

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel cannot use the ephemeral key returned by CreateSelfSigned directly.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    private static async Task WithTlsServer(
        X509Certificate2 certificate,
        Func<int, Task> test,
        Func<SslStream, CancellationToken, Task> handle)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serve = ServeAsync();
        try
        {
            await test(((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            await deadline.CancelAsync();
            listener.Stop();
            await serve;
        }

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var tls = new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
                await handle(tls, deadline.Token);
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException)
            {
                // Teardown of either end: no application envelope is owed here.
            }
        }
    }
}
