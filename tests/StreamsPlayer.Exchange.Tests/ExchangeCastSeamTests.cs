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
/// SP-0205 acceptance criteria: over the real control stream loop, cast-offers are evaluated for support,
/// answered honestly, prompted or auto-accepted per setting, and cast-stops are dispatched.
/// </summary>
public sealed class ExchangeCastSeamTests
{
    [Fact]
    public async Task UnsupportedOffer_AnswersUnsupportedWithoutPrompt()
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        var promptCalled = false;
        var answerReceived = new TaskCompletionSource<bool>();
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43),
                    ExchangeProtocol.Fingerprint(certificate.RawData), true, AutoAcceptCasts: false));
                service = new ExchangeSourceService(directory);
                service.CastPromptRequested += (off, ct) =>
                {
                    promptCalled = true;
                    return Task.FromResult(true);
                };
                service.Start();

                await answerReceived.Task;
            }, async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);

                // Send cast-offer with unsupported P2P transport
                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2,
                    type = "cast-offer",
                    castId = "cast_unsupported_1",
                    broadcastId = "b_p2p",
                    descriptor = new
                    {
                        schemaVersion = 1,
                        url = "p2p://station",
                        mode = "AUDIO_ONLY",
                        title = "P2P Radio",
                        isLive = true,
                        endpoints = new[]
                        {
                            new { url = "p2p://station", transport = "P2P", mode = "AUDIO_ONLY" }
                        }
                    }
                }, true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                var root = answer.RootElement;
                Assert.Equal("cast-answer", ExchangeProtocol.String(root, "type"));
                Assert.Equal("cast_unsupported_1", ExchangeProtocol.String(root, "castId"));
                Assert.False(root.GetProperty("accepted").GetBoolean());
                Assert.Equal("unsupported", ExchangeProtocol.String(root, "reason"));
                answerReceived.SetResult(true);
            });

            Assert.False(promptCalled);
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

    [Fact]
    public async Task AutoAccept_AnswersAcceptedImmediatelyAndRaisesEvent()
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        ExchangeCastOffer? acceptedOffer = null;
        var answerReceived = new TaskCompletionSource<bool>();
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43),
                    ExchangeProtocol.Fingerprint(certificate.RawData), true, AutoAcceptCasts: true));
                service = new ExchangeSourceService(directory);
                service.CastAccepted += off => acceptedOffer = off;
                service.Start();

                await answerReceived.Task;
                Assert.NotNull(acceptedOffer);
                Assert.Equal("cast_auto_1", acceptedOffer!.CastId);
                Assert.Equal("b_auto", acceptedOffer.BroadcastId);
            }, async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);

                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2,
                    type = "cast-offer",
                    castId = "cast_auto_1",
                    broadcastId = "b_auto",
                    fromDeviceName = "Pixel 8",
                    descriptor = new
                    {
                        schemaVersion = 1,
                        url = "http://127.0.0.1:8080/live.mp3",
                        mode = "AUDIO_ONLY",
                        title = "Auto Radio",
                        isLive = true,
                        endpoints = new[]
                        {
                            new { url = "http://127.0.0.1:8080/live.mp3", transport = "HTTP", mode = "AUDIO_ONLY" }
                        }
                    }
                }, true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                var root = answer.RootElement;
                Assert.Equal("cast-answer", ExchangeProtocol.String(root, "type"));
                Assert.Equal("cast_auto_1", ExchangeProtocol.String(root, "castId"));
                Assert.True(root.GetProperty("accepted").GetBoolean());
                answerReceived.SetResult(true);
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

    [Fact]
    public async Task PromptAccepted_AnswersAccepted()
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        var promptCalled = false;
        var answerReceived = new TaskCompletionSource<bool>();
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43),
                    ExchangeProtocol.Fingerprint(certificate.RawData), true, AutoAcceptCasts: false));
                service = new ExchangeSourceService(directory);
                service.CastPromptRequested += (off, ct) =>
                {
                    promptCalled = true;
                    return Task.FromResult(true);
                };
                service.Start();

                await answerReceived.Task;
            }, async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);

                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2,
                    type = "cast-offer",
                    castId = "cast_prompt_yes",
                    broadcastId = "b_yes",
                    descriptor = new
                    {
                        schemaVersion = 1,
                        url = "http://127.0.0.1:8080/live.mp3",
                        mode = "AUDIO_ONLY",
                        title = "Accepted Radio",
                        isLive = true,
                        endpoints = new[]
                        {
                            new { url = "http://127.0.0.1:8080/live.mp3", transport = "HTTP", mode = "AUDIO_ONLY" }
                        }
                    }
                }, true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                var root = answer.RootElement;
                Assert.Equal("cast-answer", ExchangeProtocol.String(root, "type"));
                Assert.Equal("cast_prompt_yes", ExchangeProtocol.String(root, "castId"));
                Assert.True(root.GetProperty("accepted").GetBoolean());
                answerReceived.SetResult(true);
            });

            Assert.True(promptCalled);
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

    [Fact]
    public async Task PromptDeclined_AnswersDeclined()
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        var promptCalled = false;
        var answerReceived = new TaskCompletionSource<bool>();
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43),
                    ExchangeProtocol.Fingerprint(certificate.RawData), true, AutoAcceptCasts: false));
                service = new ExchangeSourceService(directory);
                service.CastPromptRequested += (off, ct) =>
                {
                    promptCalled = true;
                    return Task.FromResult(false);
                };
                service.Start();

                await answerReceived.Task;
            }, async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);

                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2,
                    type = "cast-offer",
                    castId = "cast_prompt_no",
                    broadcastId = "b_no",
                    descriptor = new
                    {
                        schemaVersion = 1,
                        url = "http://127.0.0.1:8080/live.mp3",
                        mode = "AUDIO_ONLY",
                        title = "Declined Radio",
                        isLive = true,
                        endpoints = new[]
                        {
                            new { url = "http://127.0.0.1:8080/live.mp3", transport = "HTTP", mode = "AUDIO_ONLY" }
                        }
                    }
                }, true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                var root = answer.RootElement;
                Assert.Equal("cast-answer", ExchangeProtocol.String(root, "type"));
                Assert.Equal("cast_prompt_no", ExchangeProtocol.String(root, "castId"));
                Assert.False(root.GetProperty("accepted").GetBoolean());
                Assert.Equal("declined", ExchangeProtocol.String(root, "reason"));
                answerReceived.SetResult(true);
            });

            Assert.True(promptCalled);
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

    [Fact]
    public async Task CastStop_DispatchesEvent()
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        ExchangeCastStop? observedStop = null;
        var stopHandled = new TaskCompletionSource<bool>();
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43),
                    ExchangeProtocol.Fingerprint(certificate.RawData), true, AutoAcceptCasts: false));
                service = new ExchangeSourceService(directory);
                service.CastStopped += stop =>
                {
                    observedStop = stop;
                    stopHandled.TrySetResult(true);
                };
                service.Start();

                await stopHandled.Task;
                Assert.NotNull(observedStop);
                Assert.Equal("cast_stop_1", observedStop!.CastId);
                Assert.Equal("b_stop_1", observedStop.BroadcastId);
            }, async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);

                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2,
                    type = "cast-stop",
                    castId = "cast_stop_1",
                    broadcastId = "b_stop_1"
                }, true, token);
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

    private static async Task DoHandshakeAsync(SslStream tls, CancellationToken token)
    {
        using var opening = await ExchangeProtocol.ReadAsync(tls, false, token);
        Assert.Equal("hello", ExchangeProtocol.String(opening.RootElement, "type"));
        await ExchangeProtocol.WriteAsync(tls, new
        {
            schemaVersion = 2,
            type = "welcome",
            account = "test-login",
            publicEndpoint = "localhost:44022",
            serverVersion = "test",
            features = new[] { "directory", "cast" },
            keepaliveSeconds = 30
        }, false, token);

        using var keepalive = await ExchangeProtocol.ReadAsync(tls, true, token);
        using var list = await ExchangeProtocol.ReadAsync(tls, true, token);
        Assert.Equal("list", ExchangeProtocol.String(list.RootElement, "type"));
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }

    private static async Task WithTlsServer(
        X509Certificate2 certificate,
        Func<int, Task> test,
        Func<SslStream, CancellationToken, Task> handle)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            }
        }
    }
}
