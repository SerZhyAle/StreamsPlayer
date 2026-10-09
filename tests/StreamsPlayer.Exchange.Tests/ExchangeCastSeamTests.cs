using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0205 acceptance criteria: over the real control stream loop, cast-offers are evaluated for support,
/// answered honestly in the DEVICE-EXCHANGE 7.8 shape, prompted or auto-accepted per setting, and cast-stops
/// are dispatched; and the question to the user never stops the stream from being read.
/// </summary>
public sealed class ExchangeCastSeamTests
{
    [Fact]
    public async Task UnsupportedOffer_AnswersUnsupportedWithoutPrompt()
    {
        var promptCalled = false;
        await WithServiceAsync(autoAccept: false,
            configure: service => service.CastPromptRequested += (_, _) =>
            {
                promptCalled = true;
                return Task.FromResult(true);
            },
            test: (_, script) => script,
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, CastOffer("b_p2p", new
                {
                    schemaVersion = 1,
                    url = "p2p://station",
                    mode = "AUDIO_ONLY",
                    title = "P2P Radio",
                    isLive = true,
                    endpoints = new[] { new { url = "p2p://station", transport = "P2P", mode = "AUDIO_ONLY" } }
                }), true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                var root = answer.RootElement;
                AssertContractAnswer(root, "b_p2p");
                Assert.False(root.GetProperty("accepted").GetBoolean());
                Assert.Equal("unsupported", ExchangeProtocol.String(root, "reason"));
            });

        Assert.False(promptCalled);
    }

    [Fact]
    public async Task AutoAccept_AnswersAcceptedImmediatelyAndRaisesEvent()
    {
        ExchangeCastOffer? acceptedOffer = null;
        await WithServiceAsync(autoAccept: true,
            configure: service => service.CastAccepted += offer => acceptedOffer = offer,
            test: (_, script) => script,
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, CastOffer("b_auto", HttpAudio("Auto Radio"),
                    castId: "cast_auto_1", fromDeviceName: "Pixel 8"), true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                var root = answer.RootElement;
                // A legacy castId the offer carried is echoed; the identity is the broadcast.
                AssertContractAnswer(root, "b_auto", "cast_auto_1");
                Assert.True(root.GetProperty("accepted").GetBoolean());
            });

        await Until(() => acceptedOffer is not null);
        Assert.Equal("b_auto", acceptedOffer!.BroadcastId);
        Assert.Equal("cast_auto_1", acceptedOffer.CastId);
        Assert.Equal("Pixel 8", acceptedOffer.DeviceName);
    }

    [Fact]
    public async Task OfferInTheContractShape_IsAnsweredByBroadcastIdWithoutACastId()
    {
        // DEVICE-EXCHANGE 7.8: cast-offer is `broadcast` + `fromDeviceId`; the answer is `broadcastId`,
        // `accepted`, `reason`. A conformant server's offer has no castId and must not be dropped.
        var promptSawContractOffer = false;
        await WithServiceAsync(autoAccept: false,
            configure: service => service.CastPromptRequested += (offer, _) =>
            {
                promptSawContractOffer = offer.BroadcastId == "b_contract" && offer.CastId is null;
                return Task.FromResult(true);
            },
            test: (_, script) => script,
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, CastOffer("b_contract", HttpAudio("Contract Radio")), true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                AssertContractAnswer(answer.RootElement, "b_contract");
                Assert.True(answer.RootElement.GetProperty("accepted").GetBoolean());
            });

        Assert.True(promptSawContractOffer);
    }

    [Fact]
    public async Task PromptAccepted_AnswersAccepted()
    {
        var promptCalled = false;
        await WithServiceAsync(autoAccept: false,
            configure: service => service.CastPromptRequested += (_, _) =>
            {
                promptCalled = true;
                return Task.FromResult(true);
            },
            test: (_, script) => script,
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, CastOffer("b_yes", HttpAudio("Accepted Radio")), true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                AssertContractAnswer(answer.RootElement, "b_yes");
                Assert.True(answer.RootElement.GetProperty("accepted").GetBoolean());
            });

        Assert.True(promptCalled);
    }

    [Fact]
    public async Task PromptDeclined_AnswersDeclined()
    {
        var promptCalled = false;
        await WithServiceAsync(autoAccept: false,
            configure: service => service.CastPromptRequested += (_, _) =>
            {
                promptCalled = true;
                return Task.FromResult(false);
            },
            test: (_, script) => script,
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, CastOffer("b_no", HttpAudio("Declined Radio")), true, token);

                using var answer = await ExchangeProtocol.ReadAsync(tls, true, token);
                var root = answer.RootElement;
                AssertContractAnswer(root, "b_no");
                Assert.False(root.GetProperty("accepted").GetBoolean());
                Assert.Equal("declined", ExchangeProtocol.String(root, "reason"));
            });

        Assert.True(promptCalled);
    }

    [Fact]
    public async Task OpenPrompt_DoesNotStopTheControlStreamFromBeingRead()
    {
        // A directory push that arrives while the question is open is applied and subscribed to at once.
        // Before the fix it sat unread until the user answered, because the offer was awaited in the loop.
        var prompt = new TaskCompletionSource<bool>();
        var promptOpened = new TaskCompletionSource();
        var directoryApplied = false;
        await WithServiceAsync(autoAccept: false,
            configure: service =>
            {
                service.CastPromptRequested += (_, _) =>
                {
                    promptOpened.TrySetResult();
                    return prompt.Task;
                };
                service.DirectoryChanged += () =>
                    directoryApplied |= service.Directory?.LiveBroadcastIds.Contains("b_dir") == true;
            },
            test: async (_, script) =>
            {
                await promptOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() => directoryApplied);
                Assert.False(prompt.Task.IsCompleted);
                prompt.SetResult(true);
                await script;
            },
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, CastOffer("b_slow", HttpAudio("Slow Radio")), true, token);
                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2,
                    type = "directory",
                    revision = 1,
                    devices = new[] { new { deviceId = "phone", deviceName = "Pixel 8", presence = "online" } },
                    broadcasts = new[]
                    {
                        new { broadcastId = "b_dir", deviceId = "phone", title = "Kitchen", mode = "AUDIO_ONLY", descriptor = HttpAudio("Kitchen") }
                    }
                }, true, token);

                // The subscribe that answers the directory frame arrives before the cast answer: the loop
                // was reading while the question was open.
                var seen = new List<string?>();
                while (true)
                {
                    using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                    var type = ExchangeProtocol.String(frame.RootElement, "type");
                    seen.Add(type);
                    if (type == "cast-answer")
                    {
                        AssertContractAnswer(frame.RootElement, "b_slow");
                        break;
                    }
                }

                Assert.Contains("subscribe", seen);
            });
    }

    [Fact]
    public async Task StoppingTheService_DoesNotWaitForAPromptThatIgnoresItsToken()
    {
        // The App's prompt is a modal box; whatever it does with the token, ending the session must not
        // wait for the person to answer it.
        var promptOpened = new TaskCompletionSource();
        var promptToken = CancellationToken.None;
        ExchangeSourceService? running = null;
        await WithServiceAsync(autoAccept: false,
            configure: service =>
            {
                running = service;
                service.CastPromptRequested += (_, token) =>
                {
                    promptToken = token;
                    promptOpened.TrySetResult();
                    return new TaskCompletionSource<bool>().Task;
                };
            },
            test: async (_, _) =>
            {
                await promptOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await running!.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(promptToken.IsCancellationRequested);
            },
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, CastOffer("b_stuck", HttpAudio("Stuck Radio")), true, token);
                await Task.Delay(Timeout.Infinite, token);
            });
    }

    [Fact]
    public async Task CastStop_DispatchesEvent()
    {
        ExchangeCastStop? observedStop = null;
        var stopHandled = new TaskCompletionSource<bool>();
        await WithServiceAsync(autoAccept: false,
            configure: service => service.CastStopped += stop =>
            {
                observedStop = stop;
                stopHandled.TrySetResult(true);
            },
            test: async (_, _) => await stopHandled.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            server: async (tls, token) =>
            {
                await DoHandshakeAsync(tls, token);
                await ExchangeProtocol.WriteAsync(tls, new
                {
                    schemaVersion = 2,
                    type = "cast-stop",
                    broadcastId = "b_stop_1",
                    receiverDeviceId = "me"
                }, true, token);
                await Task.Delay(Timeout.Infinite, token);
            });

        Assert.NotNull(observedStop);
        Assert.Null(observedStop!.CastId);
        Assert.Equal("b_stop_1", observedStop.BroadcastId);
    }

    private static Dictionary<string, object?> CastOffer(
        string broadcastId, object descriptor, string? castId = null, string? fromDeviceName = null)
    {
        var frame = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["type"] = "cast-offer",
            ["fromDeviceId"] = "phone",
            ["broadcast"] = new { broadcastId, deviceId = "phone", mode = "AUDIO_ONLY", descriptor }
        };
        if (castId is not null)
        {
            frame["castId"] = castId;
        }

        if (fromDeviceName is not null)
        {
            frame["fromDeviceName"] = fromDeviceName;
        }

        return frame;
    }

    private static object HttpAudio(string title) => new
    {
        schemaVersion = 1,
        url = "http://127.0.0.1:8080/live.mp3",
        mode = "AUDIO_ONLY",
        title,
        isLive = true,
        endpoints = new[] { new { url = "http://127.0.0.1:8080/live.mp3", transport = "HTTP", mode = "AUDIO_ONLY" } }
    };

    private static void AssertContractAnswer(JsonElement root, string broadcastId, string? castId = null)
    {
        Assert.Equal("cast-answer", ExchangeProtocol.String(root, "type"));
        Assert.Equal(broadcastId, ExchangeProtocol.String(root, "broadcastId"));
        if (castId is null)
        {
            Assert.False(root.TryGetProperty("castId", out _));
        }
        else
        {
            Assert.Equal(castId, ExchangeProtocol.String(root, "castId"));
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, deadline.Token);
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

    /// <summary>
    /// Runs one enrolled service against one scripted server connection. <paramref name="test"/> receives the
    /// service and a task that ends when the server script ends (or faults with its failed assertion), so a
    /// failed assertion in the script fails the test instead of hanging it.
    /// </summary>
    private static async Task WithServiceAsync(
        bool autoAccept,
        Action<ExchangeSourceService> configure,
        Func<ExchangeSourceService, Task, Task> test,
        Func<SslStream, CancellationToken, Task> server)
    {
        using var certificate = Certificate();
        var directory = Path.Combine(Path.GetTempPath(), "StreamsPlayer.Exchange.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ExchangeSourceService? service = null;
        var script = new TaskCompletionSource();
        try
        {
            await WithTlsServer(certificate, async port =>
            {
                new ExchangeAccountStore(directory).Save(new ExchangeAccount(ExchangeProtocol.NewDeviceId(),
                    "localhost", port, "test-login", new string('t', 43),
                    ExchangeProtocol.Fingerprint(certificate.RawData), true, AutoAcceptCasts: autoAccept));
                service = new ExchangeSourceService(directory);
                configure(service);
                service.Start();
                await test(service, script.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }, async (tls, token) =>
            {
                try
                {
                    await server(tls, token);
                    script.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    script.TrySetResult();
                    throw;
                }
                catch (Exception exception)
                {
                    script.TrySetException(exception);
                    throw;
                }
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
            }
        }
    }
}
