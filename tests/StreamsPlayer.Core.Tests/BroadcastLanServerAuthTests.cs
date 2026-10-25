using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamsPlayer.Core;
using Digest = StreamsPlayer.Core.Tests.BroadcastDigestTestClient;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0223: the LAN share behind Digest, over real loopback sockets. The loopback half is the one the exchange tunnel
/// bridges to, so every connection here is the address that must not skip the check.
/// </summary>
public sealed class BroadcastLanServerAuthTests
{
    private const string AudioPath = "/live-audio.aac";
    private static readonly ExchangeRelayCredentials Listener = new("listener", "link password");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string UnprotectedHead =
        "HTTP/1.1 200 OK\r\nContent-Type: audio/aac\r\nTransfer-Encoding: chunked\r\nAccept-Ranges: none\r\nCache-Control: no-cache\r\n"
        + "icy-name: Test broadcast\r\nicy-genre: Live\r\nicy-pub: 0\r\nicy-br: 128\r\n\r\n";

    private const string NotFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    // A refusal costs nothing here unless a test is about the damper.
    private static BroadcastAttemptDamper QuickDamper() => new(step: TimeSpan.FromMilliseconds(1), ceiling: TimeSpan.FromMilliseconds(1));

    private static (BroadcastLanServer Server, int Port) StartServer(BroadcastAttemptDamper? damper = null)
    {
        var server = new BroadcastLanServer(
            new BroadcastLanServerOptions("Test broadcast", 128, 0, [AudioPath, "/live-audio"], "audio/aac", IsVideo: false),
            damper ?? QuickDamper());
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        server.Start(null, port);
        return (server, port);
    }

    private static byte[] Frame()
    {
        var frame = new byte[7 + 200];
        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = 0x40 | (4 << 2);
        frame[3] = (byte)((1 << 6) | ((frame.Length >> 11) & 0x03));
        frame[4] = (byte)(frame.Length >> 3);
        frame[5] = (byte)((frame.Length & 7) << 5 | 0x1F);
        frame[6] = 0xFC;
        for (var index = 7; index < frame.Length; index++)
        {
            frame[index] = (byte)index;
        }

        return frame;
    }

    private static async Task<(TcpClient Client, string Head)> SendAsync(int port, string method, string target, string? authorization = null)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var request = $"{method} {target} HTTP/1.1\r\nHost: localhost\r\n"
            + (authorization is null ? "" : $"Authorization: {authorization}\r\n") + "\r\n";
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request));
        return (client, await ReadHeadAsync(client.GetStream()));
    }

    /// <summary>Reads up to the blank line that ends a head, or to the end of the connection.</summary>
    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var text = new StringBuilder();
        var buffer = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buffer, timeout.Token) == 0)
            {
                break;
            }

            text.Append((char)buffer[0]);
        }

        return text.ToString();
    }

    /// <summary>Everything that arrives until the connection ends; a reset is the other way a closed connection looks.</summary>
    private static async Task<int> BytesUntilClosedAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var total = 0;
        var buffer = new byte[512];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
            }
        }
        catch (IOException)
        {
            // A connection the server closed with data unread can end as a reset; what arrived until then is the answer.
        }

        return total;
    }

    private static async Task<byte[]> ReadChunkAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var size = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, timeout.Token);
            if (one[0] == '\n')
            {
                break;
            }

            if (one[0] != '\r')
            {
                size.Append((char)one[0]);
            }
        }

        var body = new byte[Convert.ToInt32(size.ToString(), 16)];
        await stream.ReadExactlyAsync(body, timeout.Token);
        await stream.ReadExactlyAsync(new byte[2], timeout.Token);
        return body;
    }

    private static IReadOnlyList<string> ChallengeLines(string head) =>
        [.. head.Split("\r\n").Where(line => line.StartsWith("WWW-Authenticate: ", StringComparison.Ordinal)).Select(line => line["WWW-Authenticate: ".Length..])];

    private static async Task<IReadOnlyList<string>> ChallengeAsync(int port, string method = "GET", string target = AudioPath)
    {
        var (client, head) = await SendAsync(port, method, target);
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", head, StringComparison.Ordinal);
            return ChallengeLines(head);
        }
    }

    /// <summary>The client's side of the dance: take the 401's challenge on one connection, answer it on the next.</summary>
    private static async Task<(TcpClient Client, string Head)> OpenAsync(
        int port, string algorithm, ExchangeRelayCredentials pair, string method = "GET", string target = AudioPath)
    {
        var challenge = await ChallengeAsync(port, method, target);
        return await SendAsync(port, method, target, Digest.Authorize(challenge, algorithm, pair.User, pair.Password, method, target));
    }

    [Fact]
    public async Task WithNoPairsTheAnswersAreByteForByteWhatTheyWere()
    {
        var (server, port) = StartServer();
        await using var running = server;

        var (ok, head) = await SendAsync(port, "GET", AudioPath);
        using (ok)
        {
            Assert.Equal(UnprotectedHead, head);
        }

        var (unknown, unknownHead) = await SendAsync(port, "GET", "/other.mp3");
        using (unknown)
        {
            Assert.Equal(NotFound, unknownHead);
        }

        var (post, postHead) = await SendAsync(port, "POST", AudioPath);
        using (post)
        {
            Assert.Equal(NotFound, postHead);
        }

        Assert.False(server.Authenticator.Required);
    }

    [Fact]
    public async Task WithAPairARequestWithoutCredentialsGetsBothChallengesAndNotOneByteOfStream()
    {
        var (server, port) = StartServer();
        await using var running = server;
        server.SetCredentials([Listener]);

        var (client, head) = await SendAsync(port, "GET", AudioPath);
        using (client)
        {
            var lines = ChallengeLines(head);
            Assert.Equal(2, lines.Count);
            Assert.Equal(
                "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: " + lines[0] + "\r\nWWW-Authenticate: " + lines[1]
                + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                head);
            Assert.Equal("SHA-256", Digest.ParseChallenge(lines[0])["algorithm"]);
            Assert.Equal("MD5", Digest.ParseChallenge(lines[1])["algorithm"]);

            server.Enqueue(Frame());
            Assert.Equal(0, await BytesUntilClosedAsync(client.GetStream()));
        }
    }

    [Theory]
    [InlineData("SHA-256")]
    [InlineData("MD5")]
    public async Task ARightAnswerInEitherAlgorithmStreamsExactlyAsAnOpenServerDoes(string algorithm)
    {
        var (server, port) = StartServer();
        await using var running = server;
        server.SetCredentials([Listener]);

        var (client, head) = await OpenAsync(port, algorithm, Listener);
        using (client)
        {
            Assert.Equal(UnprotectedHead, head);
            var frame = Frame();
            server.Enqueue(frame);
            Assert.Equal(frame, await ReadChunkAsync(client.GetStream()));
        }
    }

    [Fact]
    public async Task AWrongPasswordAnUnknownUserAndAnAnswerForAnotherUriAreRefusedAlike()
    {
        var (server, port) = StartServer();
        await using var running = server;
        server.SetCredentials([Listener]);
        var challenge = await ChallengeAsync(port);

        var shapes = new List<string>();
        foreach (var answer in new[]
        {
            Digest.Authorize(challenge, "SHA-256", "listener", "wrong", "GET", AudioPath),
            Digest.Authorize(challenge, "SHA-256", "nobody", "link password", "GET", AudioPath, nc: 2),
            Digest.Authorize(challenge, "SHA-256", "listener", "link password", "GET", "/live-audio", nc: 3)
        })
        {
            var (client, head) = await SendAsync(port, "GET", AudioPath, answer);
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", head, StringComparison.Ordinal);
                Assert.EndsWith("Content-Length: 0\r\nConnection: close\r\n\r\n", head, StringComparison.Ordinal);
                Assert.Equal(2, ChallengeLines(head).Count);
                shapes.Add(string.Join("|", ChallengeLines(head).Select(line =>
                {
                    var fields = Digest.ParseChallenge(line);
                    return fields["realm"] + fields["qop"] + fields["algorithm"] + fields.ContainsKey("stale");
                })));
                Assert.Equal(0, await BytesUntilClosedAsync(client.GetStream()));
            }
        }

        // Nothing finer than "no": none of the three refusals differs from another in anything but its nonce.
        Assert.Single(shapes.Distinct());
    }

    [Fact]
    public async Task EveryRequestWithoutAProofGetsTheSame401WhateverItsMethodOrPath()
    {
        var (server, port) = StartServer();
        await using var running = server;
        server.SetCredentials([Listener]);

        foreach (var (method, target) in new[] { ("GET", AudioPath), ("HEAD", AudioPath), ("GET", "/no-such-path"), ("POST", AudioPath) })
        {
            var (client, head) = await SendAsync(port, method, target);
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", head, StringComparison.Ordinal);
                Assert.Equal(2, ChallengeLines(head).Count);
            }
        }

        // With a proof, the routing of an unprotected server applies again: an unknown path is a 404, and so is HEAD,
        // which this server has never served.
        var (unknown, unknownHead) = await OpenAsync(port, "SHA-256", Listener, "GET", "/no-such-path");
        using (unknown)
        {
            Assert.Equal(NotFound, unknownHead);
        }

        var (head2, headHead) = await OpenAsync(port, "MD5", Listener, "HEAD", AudioPath);
        using (head2)
        {
            Assert.Equal(NotFound, headHead);
        }
    }

    [Fact]
    public async Task RevokingAPairClosesItsListenerAtOnceAndRefusesTheOldPasswordButNotTheNewOne()
    {
        var (server, port) = StartServer();
        await using var running = server;
        server.SetCredentials([new ExchangeRelayCredentials("listener", "old password")]);
        var (client, head) = await OpenAsync(port, "SHA-256", new ExchangeRelayCredentials("listener", "old password"));
        using (client)
        {
            Assert.Equal(UnprotectedHead, head);
            var first = Frame();
            server.Enqueue(first);
            Assert.Equal(first, await ReadChunkAsync(client.GetStream()));

            server.SetCredentials([new ExchangeRelayCredentials("listener", "new password")]);
            server.Enqueue(Frame());

            // Closed at once: what may still arrive is the chunked body's terminator ("0" CRLF CRLF), never the new frame.
            Assert.Contains(await BytesUntilClosedAsync(client.GetStream()), new[] { 0, 5 });
        }

        var stale = await ChallengeAsync(port);
        var (refused, refusedHead) = await SendAsync(port, "GET", AudioPath,
            Digest.Authorize(stale, "SHA-256", "listener", "old password", "GET", AudioPath));
        using (refused)
        {
            Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", refusedHead, StringComparison.Ordinal);
        }

        var (admitted, admittedHead) = await OpenAsync(port, "SHA-256", new ExchangeRelayCredentials("listener", "new password"));
        using (admitted)
        {
            Assert.Equal(UnprotectedHead, admittedHead);
        }
    }

    [Fact]
    public async Task APairThatIsUnchangedKeepsItsListenerWhileAnotherIsAdded()
    {
        var (server, port) = StartServer();
        await using var running = server;
        server.SetCredentials([Listener]);
        var (client, _) = await OpenAsync(port, "SHA-256", Listener);
        using (client)
        {
            server.SetCredentials([Listener, new ExchangeRelayCredentials("account", "own password")]);

            var frame = Frame();
            server.Enqueue(frame);
            Assert.Equal(frame, await ReadChunkAsync(client.GetStream()));
        }
    }

    [Fact]
    public async Task AnAnonymousListenerIsClosedWhenAPairIsSetAfterItConnected()
    {
        var (server, port) = StartServer();
        await using var running = server;
        var (client, head) = await SendAsync(port, "GET", AudioPath);
        using (client)
        {
            Assert.Equal(UnprotectedHead, head);

            server.SetCredentials([Listener]);
            server.Enqueue(Frame());

            Assert.Contains(await BytesUntilClosedAsync(client.GetStream()), new[] { 0, 5 });
        }

        var (refused, refusedHead) = await SendAsync(port, "GET", AudioPath);
        using (refused)
        {
            Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", refusedHead, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ClearingThePairsOpensTheServerAndLeavesAnAuthenticatedListenerPlaying()
    {
        var (server, port) = StartServer();
        await using var running = server;
        server.SetCredentials([Listener]);
        var (client, _) = await OpenAsync(port, "MD5", Listener);
        using (client)
        {
            server.SetCredentials([]);

            var frame = Frame();
            server.Enqueue(frame);
            Assert.Equal(frame, await ReadChunkAsync(client.GetStream()));
            var (anonymous, head) = await SendAsync(port, "GET", AudioPath);
            using (anonymous)
            {
                Assert.Equal(UnprotectedHead, head);
            }
        }
    }

    [Fact]
    public async Task ARefusedGuessIsHeldBackWithoutHoldingUpAnotherConnection()
    {
        var (server, port) = StartServer(new BroadcastAttemptDamper(step: TimeSpan.FromSeconds(3), ceiling: TimeSpan.FromSeconds(3)));
        await using var running = server;
        server.SetCredentials([Listener]);
        var challenge = await ChallengeAsync(port);
        using var guess = new TcpClient();
        await guess.ConnectAsync(IPAddress.Loopback, port);
        var guessRequest = $"GET {AudioPath} HTTP/1.1\r\nHost: localhost\r\nAuthorization: "
            + Digest.Authorize(challenge, "SHA-256", "listener", "guess", "GET", AudioPath) + "\r\n\r\n";
        await guess.GetStream().WriteAsync(Encoding.UTF8.GetBytes(guessRequest));
        var pending = ReadHeadAsync(guess.GetStream());
        // The one pause: long enough for the server to have read the guess and begun to hold it back.
        await Task.Delay(200);

        // A right answer from the same address is neither held back nor queued behind the guess.
        var (good, goodHead) = await SendAsync(port, "GET", AudioPath,
            Digest.Authorize(challenge, "SHA-256", "listener", "link password", "GET", AudioPath, nc: 2));
        using (good)
        {
            Assert.Equal(UnprotectedHead, goodHead);
        }

        Assert.False(pending.IsCompleted);
        await server.StopAsync();
        Assert.Equal("", await pending.WaitAsync(Wait));
    }

    [Fact]
    public async Task ARefusedGuessIsAnsweredOnlyAfterTheDampersDelay()
    {
        var (server, port) = StartServer(new BroadcastAttemptDamper(step: TimeSpan.FromMilliseconds(400), ceiling: TimeSpan.FromMilliseconds(400)));
        await using var running = server;
        server.SetCredentials([Listener]);
        var challenge = await ChallengeAsync(port);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var (client, head) = await SendAsync(port, "GET", AudioPath, Digest.Authorize(challenge, "SHA-256", "listener", "guess", "GET", AudioPath));
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", head, StringComparison.Ordinal);
        }

        Assert.True(started.ElapsedMilliseconds >= 350, started.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public async Task AStoppedServerEndsEveryConnectionIncludingAProtectedOne()
    {
        var (server, port) = StartServer();
        server.SetCredentials([Listener]);
        var (client, _) = await OpenAsync(port, "SHA-256", Listener);
        using (client)
        {
            await server.StopAsync();

            Assert.Equal("0", Encoding.ASCII.GetString(await ReadAllAsync(client.GetStream())).Split("\r\n")[0]);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(Wait);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, timeout.Token);
        return buffer.ToArray();
    }
}
