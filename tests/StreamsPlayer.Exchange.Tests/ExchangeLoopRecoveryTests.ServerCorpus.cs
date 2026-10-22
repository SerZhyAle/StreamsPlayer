using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using StreamsPlayer.App;
using StreamsPlayer.Core;
using StreamsPlayer.Core.Tests;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0228 definition of done item 3, the part of the FMS_Win server corpora that only the real service can replay: the
/// frames of `vectors/server-frames-v2.json` (sha256 afa1a68273670f98c5dd615261759d1853a4cafd9086254e23acbdc7d0aea107,
/// contract 0.17) and `vectors/fmsw-224/frames-v2.json` (sha256 f5c6ae0db7dd47454467184c5df3378104e35bfe09609acfd37f703e833541d8,
/// contract 0.28), vendored byte for byte by the Core tests, played to an `ExchangeSourceService` over a TLS loopback. The
/// server side writes the file's server->client frames VERBATIM, never re-serialised; what the service writes back is captured
/// raw and compared with the file's client->server frames. The bytes of `hello` carry the product's own version and ids, so
/// its members are compared by name and order. Item references: AM (bounds of an enroll), AO (account kind), AT and AW
/// (sign-out, rate limit), AD (id collision retry), section 7.4 (list, subscribe, changed). Evidence only: no joint run is claimed.
/// </summary>
public sealed partial class ExchangeLoopRecoveryTests
{
    private static readonly FramesCorpus ServerCorpus = ExchangeFramesCorpus.Load(ExchangeFramesCorpus.ServerFrames);
    private static readonly FramesCorpus AccountCorpus = ExchangeFramesCorpus.Load(ExchangeFramesCorpus.AccountFrames);

    private static FramesCorpus CorpusOf(string file) => file == ExchangeFramesCorpus.ServerFrames ? ServerCorpus : AccountCorpus;

    /// <summary>One leaf for every session of this file: making a 2048-bit key per test would be most of their time.</summary>
    private static readonly Lazy<X509Certificate2> SharedCertificate = new(Certificate);

    private static async Task<byte[]> ReadRawFrame(SslStream tls, CancellationToken token)
    {
        var header = new byte[4];
        await tls.ReadExactlyAsync(header, token);
        var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        await tls.ReadExactlyAsync(body, token);
        return [.. header, .. body];
    }

    private static async Task WriteRawFrame(SslStream tls, byte[] frame, CancellationToken token)
    {
        await tls.WriteAsync(frame, token);
        await tls.FlushAsync(token);
    }

    private static string FrameType(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame.AsMemory(4));
        return ExchangeProtocol.String(document.RootElement, "type")!;
    }

    private static string[] MemberNames(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame.AsMemory(4));
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    /// <summary>The one encoding the corpus has for a client frame that carries no ids: every occurrence is the same bytes.</summary>
    private static byte[] CanonicalClientFrame(FramesCorpus corpus, string type)
    {
        var frames = corpus.Frames.Where(step => step.ClientToServer && step.Type == type).Select(step => step.Frame).ToArray();
        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.Equal(frames[0], frame));
        return frames[0];
    }

    private static Step ServerStep(FramesCorpus corpus, string transcript, string stream, string type) =>
        corpus.Named(transcript).Steps.First(step => step.Stream == stream && step.ServerToClient && step.IsFrame && step.Type == type);

    // ---------------------------------------------------------------- the control loop

    [Fact]
    public async Task ServerCorpusSession_TheControlLoopWritesTheClientFramesOfTheFile_AndReadsTheServersListAndPush()
    {
        // enroll-and-hello / s2 is the corpus' own hello-welcome-keepalive-bye stream; directory / b-control and a-control are
        // the list answer (revision 5) and the push one revision ahead (revision 6) that the same server sent.
        var welcome = ServerStep(ServerCorpus, "enroll-and-hello", "s2", "welcome");
        var keepalive = ServerStep(ServerCorpus, "enroll-and-hello", "s2", "keepalive");
        var listed = ServerStep(ServerCorpus, "directory", "b-control", "directory");
        using var listedDocument = listed.Parse();
        var revision = listedDocument.RootElement.GetProperty("revision").GetInt64();
        var pushed = ServerCorpus.Named("directory").Steps.First(step => step.ServerToClient && step.IsFrame && step.Type == "changed"
            && step.Json!.Contains($"\"revision\":{revision + 1},", StringComparison.Ordinal));
        var fileHello = ServerCorpus.Frames.First(step => step.ClientToServer && step.Type == "hello" && step.Json!.Contains("productVersion"));

        var certificate = SharedCertificate.Value;
        using var scope = new TestDirectory();
        var frames = new ConcurrentQueue<byte[]>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            frames.Enqueue(await ReadRawFrame(tls, token));
            await WriteRawFrame(tls, welcome.Frame, token);
            // The first thing the loop writes is its keepalive; the lease below is owed a list straight after it.
            frames.Enqueue(await ReadRawFrame(tls, token));
            await WriteRawFrame(tls, keepalive.Frame, token);
            var list = await ReadRawFrame(tls, token);
            frames.Enqueue(list);
            await WriteRawFrame(tls, listed.Frame, token);
            var subscribe = await ReadRawFrame(tls, token);
            frames.Enqueue(subscribe);
            await WriteRawFrame(tls, pushed.Frame, token);
            while (true)
            {
                frames.Enqueue(await ReadRawFrame(tls, token));
            }
        });
        var account = Account(server.Port, certificate);
        scope.Save(account);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            using var lease = service.AcquireDirectory(follow: true);
            await Until(() => service.Directory?.Revision == revision + 1);

            var directory = service.Directory!;
            var fileDevices = listedDocument.RootElement.GetProperty("devices").EnumerateArray()
                .Select(device => ExchangeProtocol.String(device, "deviceId")!).Order(StringComparer.Ordinal);
            Assert.Equal(fileDevices, directory.Devices!.Select(device => device.DeviceId).Order(StringComparer.Ordinal));
            Assert.Equal("ExchangeOnline", service.StatusKey);
        }
        finally
        {
            await service.StopAsync();
        }

        await Until(() => frames.Any(frame => FrameType(frame) == "bye"));
        var written = frames.ToArray();
        Assert.Equal(["hello", "keepalive", "list", "subscribe", "bye"], written.Select(FrameType));

        // hello: the members and their order are the file's; the ids are the entry's; the version is the product's.
        Assert.Equal(MemberNames(fileHello.Frame), MemberNames(written[0]));
        using (var hello = JsonDocument.Parse(written[0].AsMemory(4)))
        using (var fileHelloDocument = fileHello.Parse())
        {
            Assert.Equal(account.DeviceId, ExchangeProtocol.String(hello.RootElement, "deviceId"));
            Assert.Equal(account.Token, ExchangeProtocol.String(hello.RootElement, "deviceToken"));
            Assert.Equal(fileHelloDocument.RootElement.GetProperty("keepaliveSeconds").GetInt32(),
                hello.RootElement.GetProperty("keepaliveSeconds").GetInt32());
            Assert.Equal(fileHelloDocument.RootElement.GetProperty("schemaVersion").GetInt32(),
                hello.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        Assert.Equal(CanonicalClientFrame(ServerCorpus, "keepalive"), written[1]);
        Assert.Equal(CanonicalClientFrame(ServerCorpus, "list"), written[2]);
        Assert.Equal(CanonicalClientFrame(ServerCorpus, "subscribe"), written[3]);
        Assert.Equal(CanonicalClientFrame(ServerCorpus, "bye"), written[4]);
        server.ThrowIfScriptFailed();
    }

    // ---------------------------------------------------------------- the frames that carry the device's own id

    /// <summary>The corpus rule: ids and tokens are the scenario's, so the file's frame with the device's own id put in is the frame to expect.</summary>
    private static byte[] WithOwnId(Step step, string fileId, string ownId)
    {
        var json = step.Json!.Replace(fileId, ownId, StringComparison.Ordinal);
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task ServerCorpusSession_LeavingWritesTheFilesRevoke_ForTheDevicesOwnId()
    {
        var revoke = ServerCorpus.Frames.First(step => step.ClientToServer && step.Type == "revoke");
        using var fileRevoke = revoke.Parse();
        var fileId = ExchangeProtocol.String(fileRevoke.RootElement, "deviceId")!;

        var certificate = SharedCertificate.Value;
        using var scope = new TestDirectory();
        var frames = new ConcurrentQueue<byte[]>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            frames.Enqueue(await ReadRawFrame(tls, token));
            await WriteRawFrame(tls, ServerStep(ServerCorpus, "enroll-and-hello", "s2", "welcome").Frame, token);
            while (true)
            {
                frames.Enqueue(await ReadRawFrame(tls, token));
            }
        });
        var original = Account(server.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            service.Start();
            await Until(() => frames.Any(frame => FrameType(frame) == "keepalive"));
            await service.ForgetAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => frames.Any(frame => FrameType(frame) == "revoke"));
        }
        finally
        {
            await service.StopAsync();
        }

        Assert.Equal(WithOwnId(revoke, fileId, original.DeviceId), frames.Single(frame => FrameType(frame) == "revoke"));
        server.ThrowIfScriptFailed();
    }

    [Fact]
    public async Task ServerCorpusSession_TheTunnelBridgeSaysHelloAndAttachesAsTheFileDoes()
    {
        // tunnels: the server's `open` for a broadcast names the tunnel; the data stream that answers it is hello, welcome, attach.
        var tunnels = ServerCorpus.Named("tunnels");
        var open = tunnels.Steps.First(step => step.ServerToClient && step.Type == "open" && step.Json!.Contains("\"broadcastId\""));
        using var openDocument = open.Parse();
        var tunnel = ExchangeTunnelOpen.TryParse(openDocument.RootElement)!;
        var attach = tunnels.Steps.Single(step => step.ClientToServer && step.Type == "attach"
            && step.Json!.Contains(tunnel.TunnelId, StringComparison.Ordinal));
        var fileHello = tunnels.Steps.Single(step => step.Stream == attach.Stream && step.ClientToServer && step.Type == "hello");
        var welcome = tunnels.Steps.Single(step => step.Stream == attach.Stream && step.ServerToClient && step.Type == "welcome");

        var certificate = SharedCertificate.Value;
        using var scope = new TestDirectory();
        var frames = new ConcurrentQueue<byte[]>();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            frames.Enqueue(await ReadRawFrame(tls, token));
            await WriteRawFrame(tls, welcome.Frame, token);
            frames.Enqueue(await ReadRawFrame(tls, token));
        });
        var account = Account(server.Port, certificate);
        scope.Save(account);
        var service = new ExchangeSourceService(scope.Path);
        using var inner = ExchangeTunnelCertificate.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The server side hangs up after `attach`, so the bridge's TLS server over the attached stream has nothing to answer
        // and the run ends; what this proves is the two frames the product wrote on the way.
        await ExchangeTunnelBridge.RunAsync(service, tunnel, localPort: 1, deadline.Token, inner);

        var written = frames.ToArray();
        Assert.Equal(["hello", "attach"], written.Select(FrameType));
        Assert.Equal(MemberNames(fileHello.Frame), MemberNames(written[0]));
        Assert.Equal(attach.Frame, written[1]);
        server.ThrowIfScriptFailed();
    }

    // ---------------------------------------------------------------- hello answered by a refusal

    [Theory]
    [InlineData("refusals", "hello-revoked")]
    [InlineData("refusals", "hello-wrong")]
    [InlineData("refusals", "hello-limited")]
    [InlineData("password", "b-signed-out")]
    [InlineData("password", "b-after-delete")]
    public async Task ServerCorpusSession_AHelloAnsweredByTheFilesRefusal_EndsOrRetriesAsItemsATAndAWSay(string transcript, string stream)
    {
        var refusal = AccountCorpus.Named(transcript).Steps.Single(step => step.Stream == stream && step.ServerToClient && step.IsFrame);
        using var refusedDocument = refusal.Parse();
        var reason = ExchangeProtocol.String(refusedDocument.RootElement, "reason")!;

        var certificate = SharedCertificate.Value;
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            Assert.Equal("hello", FrameType(await ReadRawFrame(tls, token)));
            await WriteRawFrame(tls, refusal.Frame, token);
        });
        var original = Account(server.Port, certificate);
        scope.Save(original);
        var service = new ExchangeSourceService(scope.Path);
        service.Start();
        try
        {
            await Until(() => service.StatusKey == ExchangeProtocol.RefusalKey(reason));
            if (reason == "rate-limited")
            {
                // A check that was answered "too fast" is not a verdict on the device: the entry is as it was and the loop tries again.
                Assert.Equal(original.Token, service.Account.Token);
                Assert.True(service.Account.Enabled);
                await Until(() => server.Connections >= 2);
            }
            else
            {
                Assert.Null(service.Account.Token);
                if (reason == "signed-out")
                {
                    Assert.True(service.Account.SignedOut);
                    Assert.Equal(original.DeviceId, service.Account.DeviceId);
                }
                else
                {
                    // device-revoked and bad-credentials: the server does not know this device; the entry starts over.
                    Assert.False(service.Account.Enabled);
                    Assert.NotEqual(original.DeviceId, service.Account.DeviceId);
                }

                // The first retry of the backoff would be a second away: nothing is scheduled at all.
                await Task.Delay(1500);
                Assert.Equal(1, server.Connections);
            }
        }
        finally
        {
            await service.StopAsync();
        }
    }

    // ---------------------------------------------------------------- enroll answered by the file's enrolled

    public static TheoryData<string, string, string> EnrolledStreams => new()
    {
        { ExchangeFramesCorpus.ServerFrames, "enroll-and-hello", "s1" },
        { ExchangeFramesCorpus.AccountFrames, "enroll-and-hello", "s1" },
        { ExchangeFramesCorpus.AccountFrames, "registration", "member" },
        { ExchangeFramesCorpus.AccountFrames, "registration", "guest" }
    };

    [Theory]
    [MemberData(nameof(EnrolledStreams))]
    public async Task ServerCorpusSession_AnEnrollAnsweredByTheFilesEnrolled_KeepsTheServersTokenAsTheKindSays(string file, string transcript, string stream)
    {
        var corpus = CorpusOf(file);
        var enrolled = ServerStep(corpus, transcript, stream, "enrolled");
        using var enrolledDocument = enrolled.Parse();
        var token = ExchangeProtocol.String(enrolledDocument.RootElement, "deviceToken")!;
        var guest = ExchangeAccountKinds.Parse(ExchangeProtocol.String(enrolledDocument.RootElement, "accountKind")) == ExchangeAccountKind.Guest;
        // Item AM against the member set of the file's own full-form password enroll (the richest the corpus has).
        var fileEnroll = corpus.Frames.Where(step => step.ClientToServer && step.Type == "enroll" && step.Json!.Contains("\"receiver\"")
            && step.Json.Contains("\"password\"")).First();

        var certificate = SharedCertificate.Value;
        using var scope = new TestDirectory();
        byte[]? sent = null;
        await using var server = new ScriptedServer(certificate, async (_, tls, token2) =>
        {
            sent = await ReadRawFrame(tls, token2);
            await WriteRawFrame(tls, enrolled.Frame, token2);
            await HoldOpen(tls, token2);
        });
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            await service.EnrollAsync("localhost", server.Port, "alice", false, "Passw0rdTest".ToCharArray(),
                ExchangeProtocol.Fingerprint(certificate.RawData));
            Assert.Null(service.EnrollmentOutcomeKey);
            if (guest)
            {
                // Item AO: a guest never takes the own slot; its entry is kept beside, with the token the server gave.
                Assert.Null(service.Account.Token);
                var entry = Assert.Single(service.Guests.Entries);
                Assert.True(entry.Guest);
                Assert.Equal(token, entry.Token);
            }
            else
            {
                Assert.Equal(token, service.Account.Token);
                Assert.False(service.Account.Guest);
                Assert.Equal(token, new ExchangeAccountStore(scope.Path).Load().Token);
            }
        }
        finally
        {
            await service.StopAsync();
        }

        Assert.NotNull(sent);
        Assert.Equal("enroll", FrameType(sent));
        using var enroll = JsonDocument.Parse(sent.AsMemory(4));
        Assert.Empty(ExchangeEnrollmentBounds.Violations(enroll.RootElement));
        var names = enroll.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Empty(MemberNames(fileEnroll.Frame).Except(names));
        Assert.Equal(["modes", "transports"], enroll.RootElement.GetProperty("receiver").EnumerateObject().Select(property => property.Name));
        server.ThrowIfScriptFailed();
    }

    // ---------------------------------------------------------------- enroll answered by the file's refusal

    public static TheoryData<string, string, string> RefusedEnrolls()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var file in new[] { ExchangeFramesCorpus.ServerFrames, ExchangeFramesCorpus.AccountFrames })
        {
            foreach (var transcript in CorpusOf(file).Transcripts)
            {
                foreach (var stream in transcript.StreamTransports.Where(item => item.Value == "tls").Select(item => item.Key))
                {
                    var opening = transcript.Steps.Where(step => step.Stream == stream && step.IsFrame).Take(2).ToArray();
                    if (opening is [{ ClientToServer: true, Type: "enroll" }, { ServerToClient: true, Type: "refused" }])
                    {
                        data.Add(file, transcript.Name, stream);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedEnrolls))]
    public async Task ServerCorpusSession_AnEnrollAnsweredByTheFilesRefusal_ShowsTheRowOfItsReason(string file, string transcript, string stream)
    {
        var corpus = CorpusOf(file);
        var steps = corpus.Named(transcript).Steps.Where(step => step.Stream == stream && step.IsFrame).ToArray();
        using var fileEnroll = steps[0].Parse();
        var pairingCode = fileEnroll.RootElement.TryGetProperty("pairingCode", out var code);
        var refusal = steps[1];
        using var refusedDocument = refusal.Parse();
        var reason = ExchangeProtocol.String(refusedDocument.RootElement, "reason")!;

        var certificate = SharedCertificate.Value;
        using var scope = new TestDirectory();
        await using var server = new ScriptedServer(certificate, async (_, tls, token) =>
        {
            Assert.Equal("enroll", FrameType(await ReadRawFrame(tls, token)));
            await WriteRawFrame(tls, refusal.Frame, token);
        });
        var service = new ExchangeSourceService(scope.Path);
        try
        {
            await service.EnrollAsync("localhost", server.Port, ExchangeProtocol.String(fileEnroll.RootElement, "login")!, pairingCode,
                (pairingCode ? code.GetString()! : "Passw0rdTest").ToCharArray(), ExchangeProtocol.Fingerprint(certificate.RawData));
            Assert.Equal(ExchangeProtocol.RefusalKey(reason, oneTimeCode: pairingCode), service.EnrollmentOutcomeKey);
            Assert.Null(service.Account.Token);
            // Item AD: a fresh id the server calls a collision is replaced once and the join is retried; no other refusal is.
            Assert.Equal(reason == "id-collision" ? 2 : 1, server.Connections);
        }
        finally
        {
            await service.StopAsync();
        }

        server.ThrowIfScriptFailed();
    }
}
