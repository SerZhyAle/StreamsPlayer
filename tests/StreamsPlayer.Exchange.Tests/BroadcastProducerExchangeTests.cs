using System.Collections.Concurrent;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0217's producer over the real control-stream protocol, against the same scripted TLS server the
/// account tests use: the producer's envelopes ride the one stream the loop owns, the server's answers
/// reach the producer (a broadcast start and its refusal), and the leave ordering holds - the
/// `broadcast-end` goes out while the control stream is still alive, before the leave's own `revoke`.
/// </summary>
public sealed partial class ExchangeLoopRecoveryTests
{
    [Fact]
    public async Task ProducerEnvelope_RidesTheControlStreamAndItsAnswerArrives()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var seen = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type == "broadcast-start")
                {
                    var broadcastId = ExchangeProtocol.String(frame.RootElement.GetProperty("broadcast"), "broadcastId")!;
                    await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "broadcast-started", broadcastId }, true, token);
                }
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => seen.Contains("keepalive"));

            var answer = service.WaitForBroadcastStartAnswerAsync("test-broadcast", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(await service.PostControlEnvelopeAsync(new
            {
                schemaVersion = 2, type = "broadcast-start",
                broadcast = new { broadcastId = "test-broadcast", deviceId = service.Account.DeviceId, mode = "AUDIO_ONLY" },
                maxListeners = 0
            }, CancellationToken.None));
            Assert.Null(await answer);
            Assert.Contains("broadcast-start", seen);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ARefusedBroadcastStart_IsTheProducersAnswerNotTheSessionsEnd()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var seen = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            var refusedCount = 0;
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type == "broadcast-start")
                {
                    var broadcastId = ExchangeProtocol.String(frame.RootElement.GetProperty("broadcast"), "broadcastId")!;
                    if (refusedCount++ == 0)
                    {
                        await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "capacity", broadcastId }, true, token);
                    }
                    else
                    {
                        await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "broadcast-started", broadcastId }, true, token);
                    }
                }
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => seen.Contains("keepalive"));

            var refused = service.WaitForBroadcastStartAnswerAsync("refused-broadcast", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(await service.PostControlEnvelopeAsync(new
            {
                schemaVersion = 2, type = "broadcast-start",
                broadcast = new { broadcastId = "refused-broadcast", deviceId = service.Account.DeviceId, mode = "AUDIO_ONLY" },
                maxListeners = 0
            }, CancellationToken.None));
            Assert.Equal("capacity", await refused);

            // The session survived the refusal: a second start on the same stream is answered.
            var started = service.WaitForBroadcastStartAnswerAsync("second-broadcast", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(await service.PostControlEnvelopeAsync(new
            {
                schemaVersion = 2, type = "broadcast-start",
                broadcast = new { broadcastId = "second-broadcast", deviceId = service.Account.DeviceId, mode = "AUDIO_ONLY" },
                maxListeners = 0
            }, CancellationToken.None));
            Assert.Null(await started);
            Assert.Equal("ExchangeOnline", service.StatusKey);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    private static object StartEnvelope(ExchangeSourceService service, string broadcastId, string type = "broadcast-start") => new
    {
        schemaVersion = 2, type,
        broadcast = new { broadcastId, deviceId = service.Account.DeviceId, mode = "AUDIO_ONLY" },
        maxListeners = 0
    };

    [Fact]
    public async Task AnUnaddressedRefusal_IsTheStartInFlightsAnswer_NotTheSessionsEnd()
    {
        // Every refusal in the server corpus (7.4) carries the reason alone: the broadcast is the one in flight.
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var seen = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            var starts = 0;
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type != "broadcast-start")
                {
                    continue;
                }

                var broadcastId = ExchangeProtocol.String(frame.RootElement.GetProperty("broadcast"), "broadcastId")!;
                if (starts++ == 0)
                {
                    await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "capacity" }, true, token);
                }
                else
                {
                    await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "broadcast-started", broadcastId }, true, token);
                }
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => seen.Contains("keepalive"));

            var refused = service.WaitForBroadcastStartAnswerAsync("refused-broadcast", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(await service.PostControlEnvelopeAsync(StartEnvelope(service, "refused-broadcast"), CancellationToken.None));
            Assert.Equal("capacity", await refused);
            Assert.Equal("ExchangeOnline", service.StatusKey);

            var started = service.WaitForBroadcastStartAnswerAsync("second-broadcast", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(await service.PostControlEnvelopeAsync(StartEnvelope(service, "second-broadcast"), CancellationToken.None));
            Assert.Null(await started);
            Assert.Equal(1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task AnUnaddressedSessionLevelRefusal_StillEndsTheSession_EvenWithAStartInFlight()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var seen = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type == "broadcast-start")
                {
                    await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "device-revoked" }, true, token);
                }
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => seen.Contains("keepalive"));

            var answer = service.WaitForBroadcastStartAnswerAsync("revoked-broadcast", TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.True(await service.PostControlEnvelopeAsync(StartEnvelope(service, "revoked-broadcast"), CancellationToken.None));
            await Until(() => service.StatusKey == "ExchangeDeviceRevoked");

            Assert.Null(service.Account.Token);
            await Assert.ThrowsAsync<TimeoutException>(() => answer);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task AnUnaddressedBroadcastRefusal_WithNothingInFlight_IsNotSwallowed()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            using var keepalive = await ExchangeProtocol.ReadAsync(tls, true, token);
            await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "capacity" }, true, token);
            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();

            // The session is the server's to refuse when no request of ours is waiting for the answer.
            await Until(() => server.Connections >= 2);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task AnUnaddressedRefusal_OfABroadcastUpdate_DoesNotEndTheSession()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var seen = new ConcurrentQueue<string>();
        var refusalSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type == "broadcast-update")
                {
                    await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "refused", reason = "capacity" }, true, token);
                    refusalSent.TrySetResult();
                }
                else if (type == "broadcast-start")
                {
                    var broadcastId = ExchangeProtocol.String(frame.RootElement.GetProperty("broadcast"), "broadcastId")!;
                    await ExchangeProtocol.WriteAsync(tls, new { schemaVersion = 2, type = "broadcast-started", broadcastId }, true, token);
                }
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => seen.Contains("keepalive"));

            Assert.True(await service.PostBroadcastUpdateAsync(StartEnvelope(service, "updated-broadcast", "broadcast-update"), CancellationToken.None));
            await refusalSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(500);

            Assert.Equal("ExchangeOnline", service.StatusKey);
            var started = service.WaitForBroadcastStartAnswerAsync("after-update", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.True(await service.PostControlEnvelopeAsync(StartEnvelope(service, "after-update"), CancellationToken.None));
            Assert.Null(await started);
            Assert.Equal(1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task Leave_EndsTheBroadcastBeforeTheRevoke_WhileTheStreamIsAlive()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var order = new ConcurrentQueue<string>();
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                order.Enqueue(type);
                if (type == "revoke")
                {
                    revoked.TrySetResult();
                }
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => order.Contains("keepalive"));

            // The producer's leave hook: the broadcast ends on the live control stream, before the leave
            // winds the session down - broadcast-end precedes the revoke (constraint 6; SP-0214 sends no bye).
            service.BeforeLeaveAsync = async () =>
            {
                await service.PostControlEnvelopeAsync(new
                {
                    schemaVersion = 2, type = "broadcast-end", broadcastId = "live-broadcast"
                }, CancellationToken.None);
            };

            await service.ForgetAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var sequence = order.ToArray();
            Assert.Contains("broadcast-end", sequence);
            var end = Array.IndexOf(sequence, "broadcast-end");
            var revocation = Array.IndexOf(sequence, "revoke");
            Assert.True(end < revocation, $"broadcast-end at {end}, revoke at {revocation}: {string.Join(",", sequence)}");
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task AQuitEndsTheBroadcastBeforeTheBye()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        var order = new ConcurrentQueue<string>();
        var bye = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                order.Enqueue(type);
                if (type == "bye")
                {
                    bye.TrySetResult();
                }
            }
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => order.Contains("keepalive"));

            await service.PostControlEnvelopeAsync(new
            {
                schemaVersion = 2, type = "broadcast-start",
                broadcast = new { broadcastId = "quit-broadcast", deviceId = service.Account.DeviceId, mode = "AUDIO_ONLY" },
                maxListeners = 0
            }, CancellationToken.None);

            // The quit path as the application runs it (App.CompleteExit): the producer ends the
            // broadcast while the control stream is alive, and only then the account says goodbye.
            Assert.True(await service.PostControlEnvelopeAsync(new
            {
                schemaVersion = 2, type = "broadcast-end", broadcastId = "quit-broadcast"
            }, CancellationToken.None));

            var stopping = service.StopAsync();
            await bye.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));

            var sequence = order.ToArray();
            var end = Array.IndexOf(sequence, "broadcast-end");
            var farewell = Array.IndexOf(sequence, "bye");
            Assert.True(end >= 0 && farewell >= 0 && end < farewell,
                $"broadcast-end at {end}, bye at {farewell}: {string.Join(",", sequence)}");
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ACastResultReachesTheProducer_WithItsOwnReasonMessage()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            await ExchangeProtocol.WriteAsync(tls, new
            {
                schemaVersion = 2, type = "cast-result", broadcastId = "b1", receiverDeviceId = "r1",
                accepted = false, reason = "declined"
            }, true, token);
            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        var result = new TaskCompletionSource<ExchangeCastResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            service.CastResult += received => result.TrySetResult(received);
            service.Start();
            var cast = await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(cast.Accepted);
            Assert.Equal("BroadcastCastDeclined", cast.MessageKey);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ATunnelOpenReachesTheProducer()
    {
        using var certificate = Certificate();
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            await Handshake(tls, token);
            await ExchangeProtocol.WriteAsync(tls, new
            {
                schemaVersion = 2, type = "open", tunnelId = "tunnel-1", broadcastId = "b1", scheme = "http"
            }, true, token);
            await HoldOpen(tls, token);
        });
        scope.Save(Account(server.Port, certificate));
        var service = new ExchangeSourceService(scope.Path);
        var opened = new TaskCompletionSource<ExchangeTunnelOpen>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            service.TunnelRequested += (tunnel, _) => opened.TrySetResult(tunnel);
            service.Start();
            var tunnel = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("tunnel-1", tunnel.TunnelId);
            Assert.Equal("b1", tunnel.BroadcastId);
            Assert.Equal("http", tunnel.Scheme);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }
}
