using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0223 phase 5 on the wire (LIVE-BROADCAST item R, `DEVICE-EXCHANGE` items BF and DV): the evidence a broadcaster owes - an
/// update captured after a change to no password carries `"verifiers": []`, one captured after a change back carries one entry per
/// pair - read from the frames the producer really sends, with the pair's PBKDF2 recomputed here from the password the person is told.
/// </summary>
public sealed partial class ExchangeLoopRecoveryTests
{
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int Status, string Head)> LanGetAsync(int port, string path)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
        var buffer = new byte[4096];
        var head = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return (int.Parse(head.ToString().Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), head.ToString());
    }

    private static bool VerifierMatches(JsonElement verifier, string password)
    {
        Assert.True(ExchangeBase64Url.TryDecode(verifier.GetProperty("salt").GetString(), out var salt));
        Assert.True(ExchangeBase64Url.TryDecode(verifier.GetProperty("hash").GetString(), out var hash));
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);
        var expected = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 600_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, hash);
    }

    [Fact]
    public async Task ChangingTheLinkChoiceOnTheAir_SendsTheVerifiersAndRevokesThePreviousPassword()
    {
        using var certificate = Certificate();
        using var sender = ExchangeE2eKeyPair.Generate();
        using var scope = new TestDirectory();
        var seen = new ConcurrentQueue<string>();
        var updates = new ConcurrentQueue<string>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            using var hello = await ExchangeProtocol.ReadAsync(tls, false, token);
            await ExchangeProtocol.WriteAsync(tls, new
            {
                schemaVersion = 2, type = "welcome", account = "test-login", accountKind = "member",
                publicEndpoint = "localhost:44022", serverVersion = "test",
                features = new[] { "directory", "sealed", "tunnel", "relay" }, keepaliveSeconds = 30
            }, false, token);
            while (true)
            {
                using var frame = await ExchangeProtocol.ReadAsync(tls, true, token);
                var type = ExchangeProtocol.String(frame.RootElement, "type")!;
                seen.Enqueue(type);
                if (type == "broadcast-update")
                {
                    updates.Enqueue(frame.RootElement.GetRawText());
                }
            }
        });
        scope.Save(Account(server.Port, certificate).WithE2eKey(sender));
        var exchange = new ExchangeSourceService(scope.Path);
        using var producer = new BroadcastProducerService(scope.Path, exchange);
        using var sealing = new BroadcastSealing("link-wire", "video/mp2t");
        var port = FreeLoopbackPort();
        const string token = "0123456789abcdef0123456789abcdef";
        var path = BroadcastDescriptorWriter.LanPath(BroadcastSource.Screen, token);
        var lan = new BroadcastLanServer(new BroadcastLanServerOptions(
            "PC screen", 128, 0, [path], BroadcastDescriptorWriter.VideoContentType, IsVideo: true));
        lan.Start(null, port);
        var state = new BroadcastRunningSource
        {
            Source = BroadcastSource.Screen, BroadcastId = "link-wire", Title = "PC screen", StartedAt = DateTimeOffset.UtcNow,
            Encoder = null!, Capture = null!, Frames = new AdtsFrameStream(), OpaqueToken = token, LocalPort = port,
            ExchangeStarted = true, Announced = true, Sealing = sealing, LanServer = lan
        };
        var states = (Dictionary<BroadcastSource, BroadcastRunningSource>)typeof(BroadcastProducerService)
            .GetField("_running", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(producer)!;

        JsonDocument Next()
        {
            Assert.True(updates.TryDequeue(out var raw), "no broadcast-update was sent");
            return JsonDocument.Parse(raw!);
        }

        try
        {
            exchange.Start();
            await Until(() => seen.Contains("keepalive"));
            states[BroadcastSource.Screen] = state;

            // 1. "Made for each broadcast": two pairs, the descriptor carries the marker, the LAN server asks for a password.
            await producer.SetLinkChoiceAsync(BroadcastLinkChoice.Generated, null);
            await Until(() => !updates.IsEmpty);
            var generated = producer.LinkPassword(BroadcastSource.Screen);
            Assert.NotNull(generated);
            using (var first = Next())
            {
                var verifiers = first.RootElement.GetProperty("verifiers").EnumerateArray().ToArray();
                Assert.Equal(["account", "listener"], verifiers.Select(v => v.GetProperty("user").GetString()).Order());
                Assert.True(VerifierMatches(verifiers.Single(v => v.GetProperty("user").GetString() == "listener"), generated!));
                var auth = first.RootElement.GetProperty("broadcast").GetProperty("descriptor").GetProperty("auth");
                Assert.Equal("password-v1", auth.GetProperty("scheme").GetString());
                Assert.Equal("listener", auth.GetProperty("user").GetString());
            }

            var (protectedStatus, challenge) = await LanGetAsync(port, path);
            Assert.Equal(401, protectedStatus);
            Assert.Equal(2, challenge.Split("\r\n").Count(line => line.StartsWith("WWW-Authenticate: Digest", StringComparison.OrdinalIgnoreCase)));

            // 2. "No password": an empty list says the relay checks nobody; no marker; the LAN server lets anyone in.
            await producer.SetLinkChoiceAsync(BroadcastLinkChoice.None, null);
            await Until(() => !updates.IsEmpty);
            using (var none = Next())
            {
                Assert.Equal(JsonValueKind.Array, none.RootElement.GetProperty("verifiers").ValueKind);
                Assert.Equal(0, none.RootElement.GetProperty("verifiers").GetArrayLength());
                Assert.False(none.RootElement.GetProperty("broadcast").GetProperty("descriptor").TryGetProperty("auth", out _));
            }

            Assert.Null(producer.LinkPassword(BroadcastSource.Screen));
            Assert.Equal(200, (await LanGetAsync(port, path)).Status);

            // 3. "Set by me": one pair per account and link again, from the password the person typed.
            const string chosen = "my own password";
            await producer.SetLinkChoiceAsync(BroadcastLinkChoice.Custom, chosen);
            await Until(() => !updates.IsEmpty);
            using (var custom = Next())
            {
                var verifiers = custom.RootElement.GetProperty("verifiers").EnumerateArray().ToArray();
                Assert.Equal(2, verifiers.Length);
                Assert.True(VerifierMatches(verifiers.Single(v => v.GetProperty("user").GetString() == "listener"), chosen));
                Assert.False(VerifierMatches(verifiers.Single(v => v.GetProperty("user").GetString() == "listener"), generated!));
            }

            Assert.Equal(chosen, producer.LinkPassword(BroadcastSource.Screen));
            Assert.Equal(401, (await LanGetAsync(port, path)).Status);

            // The typed password is in the platform secret store and nowhere in clear on disk.
            foreach (var file in Directory.GetFiles(scope.Path))
            {
                Assert.DoesNotContain(chosen, Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
            }

            Assert.True(File.Exists(Path.Combine(scope.Path, "broadcast-link-password.bin")));

            // 4. Back to a generated password and a renewal: the previous one no longer matches what the relay was told.
            await producer.SetLinkChoiceAsync(BroadcastLinkChoice.Generated, null);
            await Until(() => !updates.IsEmpty);
            Next().Dispose();
            var before = producer.LinkPassword(BroadcastSource.Screen)!;
            await producer.RenewLinkPasswordAsync();
            await Until(() => !updates.IsEmpty);
            var after = producer.LinkPassword(BroadcastSource.Screen)!;
            Assert.NotEqual(before, after);
            using (var renewed = Next())
            {
                var link = renewed.RootElement.GetProperty("verifiers").EnumerateArray().Single(v => v.GetProperty("user").GetString() == "listener");
                Assert.True(VerifierMatches(link, after));
                Assert.False(VerifierMatches(link, before));
            }
        }
        finally
        {
            states.Clear();
            await lan.StopAsync();
            await exchange.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }
}
