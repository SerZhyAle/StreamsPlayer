using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ExchangeProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16385)]
    [InlineData(uint.MaxValue)]
    public async Task InvalidOpeningLength_ClosesWithoutAllocating(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExchangeProtocol.ReadAsync(stream, false, default));
        Assert.False(stream.CanRead);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{}{}")]
    [InlineData("{invalid}")]
    public async Task NonObjectOrInvalidBody_Closes(string body)
    {
        using var stream = Frame(body);
        await Assert.ThrowsAnyAsync<Exception>(() => ExchangeProtocol.ReadAsync(stream, false, default));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task OpeningBoundary_IsIncludedButLargerFrameNeedsAuthentication()
    {
        using var exact = Frame("{\"padding\":\"" + new string('x', ExchangeProtocol.OpeningLimit - 14) + "\"}");
        using var opening = await ExchangeProtocol.ReadAsync(exact, false, default);
        Assert.Equal(ExchangeProtocol.OpeningLimit - 14, opening.RootElement.GetProperty("padding").GetString()!.Length);
        using var large = Frame("{\"padding\":\"" + new string('x', 20000) + "\"}");
        using var authenticated = await ExchangeProtocol.ReadAsync(large, true, default);
        Assert.Equal(20000, authenticated.RootElement.GetProperty("padding").GetString()!.Length);
        using var oversize = Frame("{\"padding\":\"" + new string('x', ExchangeProtocol.AuthenticatedLimit) + "\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => ExchangeProtocol.ReadAsync(oversize, true, default));
        Assert.False(oversize.CanRead);
    }

    [Fact]
    public async Task Envelope_RoundTripsUnknownMembersAndType()
    {
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteAsync(stream, new { schemaVersion = 2, type = "future", extra = new { value = "привет" } }, false, default);
        stream.Position = 0;
        using var frame = await ExchangeProtocol.ReadAsync(stream, false, default);
        Assert.Equal("future", ExchangeProtocol.String(frame.RootElement, "type"));
        Assert.Equal("привет", frame.RootElement.GetProperty("extra").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("pairingCode")]
    public async Task Enrollment_WritesOnlySelectedProofAndWipesMutableSecret(string member)
    {
        var secret = "Aa secret 1234".ToCharArray();
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteEnrollmentAsync(stream, new { schemaVersion = 2, type = "enroll" }, member, secret, default);
        Assert.All(secret, character => Assert.Equal('\0', character));
        stream.Position = 0;
        using var frame = await ExchangeProtocol.ReadAsync(stream, false, default);
        Assert.Equal("Aa secret 1234", frame.RootElement.GetProperty(member).GetString());
        Assert.False(frame.RootElement.TryGetProperty(member == "password" ? "pairingCode" : "password", out _));
    }

    [Fact]
    public async Task Enrollment_WriteFailureAlsoWipesSecret()
    {
        var secret = "secret".ToCharArray();
        using var stream = new MemoryStream(new byte[1], writable: false);
        await Assert.ThrowsAsync<NotSupportedException>(() => ExchangeProtocol.WriteEnrollmentAsync(stream,
            new { schemaVersion = 2, type = "enroll" }, "password", secret, default));
        Assert.All(secret, character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData("bad-credentials", "ExchangeBadCredentials")]
    [InlineData("device-revoked", "ExchangeDeviceRevoked")]
    [InlineData("version-unsupported", "ExchangeVersionUnsupported")]
    [InlineData("tls-required", "ExchangeTlsRequired")]
    [InlineData("id-collision", "ExchangeIdCollision")]
    [InlineData("capacity", "ExchangeCapacity")]
    [InlineData("port-unavailable", "ExchangePortUnavailable")]
    [InlineData("unavailable", "ExchangeUnavailable")]
    [InlineData("rate-limited", "ExchangeRateLimited")]
    [InlineData("future", "ExchangeRefused")]
    [InlineData(null, "ExchangeRefused")]
    public void AllRefusals_MapToDistinctKeysWithGenericFallback(string? reason, string key) =>
        Assert.Equal(key, ExchangeProtocol.RefusalKey(reason));

    [Fact]
    public void Capabilities_AdvertiseOnlyExistingPlaybackPaths()
    {
        // SP-0203 requirement 6: exactly the pairs a playback path opens. HTTP video is the MPEG-TS
        // shape in the player window; RELAY audio rides the pinned HTTP client, RELAY video the
        // engine (through the pin proxy when the descriptor pins the certificate); TUNNEL rides the
        // SP-0204 forwarder; P2P stays reserved and unimplemented.
        Assert.Equal(new[]
        {
            "AUDIO_ONLY:HTTP", "VIDEO_ONLY:HTTP", "VIDEO_AUDIO:HTTP",
            "VIDEO_ONLY:RTSP", "VIDEO_AUDIO:RTSP",
            "AUDIO_ONLY:RELAY", "VIDEO_ONLY:RELAY", "VIDEO_AUDIO:RELAY",
            "AUDIO_ONLY:TUNNEL", "VIDEO_ONLY:TUNNEL", "VIDEO_AUDIO:TUNNEL"
        },
            ExchangeCapabilities.Plays.Select(pair => $"{pair.Mode}:{pair.Transport}"));
        foreach (var pair in ExchangeCapabilities.Plays)
        {
            var url = pair.Transport switch
            {
                "HTTP" when pair.Mode == FastMediaSorterBroadcastDescriptor.AudioOnlyMode => "http://localhost/audio",
                "HTTP" => "http://localhost/live.ts",
                "RTSP" => "rtsp://localhost/video",
                "RELAY" => "https://relay.example.net:44022/v2/b/b1/stream",
                "TUNNEL" when pair.Mode == FastMediaSorterBroadcastDescriptor.AudioOnlyMode => "fmsx://localhost:44022/b/b1/http",
                _ => "fmsx://localhost:44022/b/b1/rtsp"
            };
            var inner = pair.Transport == "TUNNEL"
                ? (pair.Mode == FastMediaSorterBroadcastDescriptor.AudioOnlyMode ? "http://127.0.0.1/audio" : "rtsp://127.0.0.1/video")
                : null;
            // SP-0226: an exchange pair plays only as a sealed endpoint (items DK and DP), so the pair is offered as one.
            var endpoint = new FastMediaSorterBroadcastEndpoint(
                url,
                pair.Transport, pair.Mode, null, null, null, null, true, 500, null, inner,
                pair.Transport is "RELAY" or "TUNNEL" ? new ExchangeEndpointSeal(ExchangeEndpointSeal.CurrentVersion) : null);
            var descriptor = new FastMediaSorterBroadcast("http://localhost/fallback", pair.Mode, "", null, true, 500, [endpoint]);
            Assert.Equal(endpoint, descriptor.SelectPlaybackEndpoint());
            // And the persisted form a reconnect leg reads offers the same pair as an attempt.
            var info = new FastMediaSorterBroadcastInfo { Mode = pair.Mode, Endpoints = [endpoint] };
            Assert.Equal([endpoint], info.PlaybackAttemptEndpoints());
        }

        // Item AM: 3 modes x 4 transports is 12 pairs, over the bound of 8, so the declaration omits `plays`
        // (item E then reads the two lists as that product) while the 11 pairs stay in `Plays` for cast answers.
        using var receiver = JsonSerializer.SerializeToDocument(ExchangeCapabilities.Receiver, ExchangeProtocol.JsonOptions);
        Assert.False(receiver.RootElement.TryGetProperty("plays", out _));
        Assert.Equal(3, receiver.RootElement.GetProperty("modes").GetArrayLength());
        Assert.Equal(4, receiver.RootElement.GetProperty("transports").GetArrayLength());
    }

    [Fact]
    public void ADeclarationWithinTheBoundOfItemAM_StillWritesItsPairs()
    {
        var small = new[]
        {
            new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.AudioOnlyMode, "HTTP"),
            new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.AudioOnlyMode, "RELAY"),
            new ExchangePlayPair(FastMediaSorterBroadcastDescriptor.VideoOnlyMode, "HTTP")
        };

        using var declared = JsonSerializer.SerializeToDocument(ExchangeCapabilities.Declare(small), ExchangeProtocol.JsonOptions);

        var plays = declared.RootElement.GetProperty("plays").EnumerateArray()
            .Select(pair => $"{pair.GetProperty("mode").GetString()}:{pair.GetProperty("transport").GetString()}");
        Assert.Equal(["AUDIO_ONLY:HTTP", "AUDIO_ONLY:RELAY", "VIDEO_ONLY:HTTP"], plays);
    }

    [Fact]
    public void TheProductAtTheBoundIsDeclaredAndOneOverItIsNot()
    {
        ExchangePlayPair[] Grid(int modes, int transports) =>
        [
            .. Enumerable.Range(0, modes).SelectMany(mode =>
                Enumerable.Range(0, transports).Select(transport => new ExchangePlayPair($"M{mode}", $"T{transport}")))
        ];

        using var atBound = JsonSerializer.SerializeToDocument(ExchangeCapabilities.Declare(Grid(2, 4)), ExchangeProtocol.JsonOptions);
        using var overBound = JsonSerializer.SerializeToDocument(ExchangeCapabilities.Declare(Grid(3, 3)), ExchangeProtocol.JsonOptions);

        Assert.Equal(8, atBound.RootElement.GetProperty("plays").GetArrayLength());
        Assert.False(overBound.RootElement.TryGetProperty("plays", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheEnrollFrameStaysWithinTheBoundsOfItemAM(bool keyed)
    {
        using var deviceKey = keyed ? ExchangeE2eKeyPair.Generate() : null;
        var fields = ExchangeEnrollmentFields.Build("alice", ExchangeProtocol.NewDeviceId(), "STREAMS Player",
            "streamsplayer-windows", "26.1021.1200", "windows", ["receiver", "broadcaster"],
            ExchangeCapabilities.Receiver, 30, deviceKey);
        using var stream = new MemoryStream();

        await ExchangeProtocol.WriteEnrollmentAsync(stream, fields, "password", "Aa secret 1234".ToCharArray(), default);

        stream.Position = 0;
        using var frame = await ExchangeProtocol.ReadAsync(stream, false, default);
        var receiver = frame.RootElement.GetProperty("receiver");
        foreach (var list in new[] { "modes", "transports", "plays" })
        {
            if (receiver.TryGetProperty(list, out var entries))
            {
                Assert.InRange(entries.GetArrayLength(), 1, 8);
            }
        }

        foreach (var entry in receiver.GetProperty("modes").EnumerateArray().Concat(receiver.GetProperty("transports").EnumerateArray()))
        {
            Assert.InRange(entry.GetString()!.Length, 1, 16);
        }

        if (receiver.TryGetProperty("plays", out var pairs))
        {
            Assert.All(pairs.EnumerateArray(), pair =>
            {
                Assert.InRange(pair.GetProperty("mode").GetString()!.Length, 1, 16);
                Assert.InRange(pair.GetProperty("transport").GetString()!.Length, 1, 16);
            });
        }

        var modes = receiver.GetProperty("modes").GetArrayLength();
        var transports = receiver.GetProperty("transports").GetArrayLength();
        if (modes * transports > 8)
        {
            Assert.False(receiver.TryGetProperty("plays", out _));
        }

        foreach (var list in new[] { "roles", "capabilities" })
        {
            if (frame.RootElement.TryGetProperty(list, out var entries))
            {
                Assert.InRange(entries.GetArrayLength(), 1, 8);
                Assert.All(entries.EnumerateArray(), entry => Assert.InRange(entry.GetString()!.Length, 1, 32));
            }
        }

        Assert.Equal(keyed, frame.RootElement.TryGetProperty("capabilities", out _));
        Assert.Equal(22, frame.RootElement.GetProperty("deviceId").GetString()!.Length);
    }

    [Fact]
    public void DeviceIdentity_IsShareIdFormAndRandom()
    {
        var id = ExchangeProtocol.NewDeviceId();
        Assert.Matches("^[A-Za-z0-9_-]{22}$", id);
        Assert.NotEqual(id, ExchangeProtocol.NewDeviceId());
        Assert.Equal(16, Convert.FromBase64String(id.Replace('-', '+').Replace('_', '/') + "==").Length);
    }

    [Fact]
    public void DiagnosticRedaction_CoversAccountFieldsAndBareFingerprint()
    {
        var fingerprint = ExchangeProtocol.Fingerprint([1, 2, 3]);
        var text = "{\"login\":\"private-user\",\"deviceToken\":\"private-token\",\"pairingCode\":\"ABCD1234\",\"exchangeAddress\":\"server.local\",\"password\":\"a\\\"b\"} " + fingerprint;
        var redacted = ExchangeDiagnosticRedactor.Redact(text);
        foreach (var secret in new[] { "private-user", "private-token", "ABCD1234", "server.local", "a\\\"b", fingerprint })
        {
            Assert.DoesNotContain(secret, redacted);
        }

        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Refusals_ArePresentInEveryShippedLanguage()
    {
        string[] reasons = ["bad-credentials", "device-revoked", "version-unsupported", "tls-required",
            "id-collision", "capacity", "port-unavailable", "unavailable", "rate-limited", "future"];
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            foreach (var reason in reasons)
            {
                var key = ExchangeProtocol.RefusalKey(reason);
                Assert.True(dictionary.Values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text), $"{dictionary.Code}:{key}");
            }
        }
    }

    [Fact]
    public async Task Opening_IgnoresFutureTypeThenAcceptsWelcome()
    {
        using var stream = new MemoryStream();
        await ExchangeProtocol.WriteAsync(stream, new { schemaVersion = 2, type = "future", member = "ignored" }, false, default);
        await ExchangeProtocol.WriteAsync(stream, new
        {
            schemaVersion = 2, type = "welcome", account = "test", publicEndpoint = "localhost:1234",
            serverVersion = "fixture", keepaliveSeconds = 30, features = new[] { "future-feature" }
        }, false, default);
        stream.Position = 0;
        using var opening = await ExchangeHandshake.ReadAsync(stream, "welcome", default);
        Assert.Equal("welcome", ExchangeProtocol.String(opening.RootElement, "type"));
        Assert.Equal(30, ExchangeHandshake.ReadInterval(opening.RootElement));
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"type\":\"enroll\"}")]
    [InlineData("{\"schemaVersion\":1,\"type\":\"welcome\"}")]
    [InlineData("{\"schemaVersion\":\"2\",\"type\":\"welcome\"}")]
    [InlineData("{\"schemaVersion\":2,\"type\":1}")]
    public async Task Opening_InvalidSchemaOrKnownWrongStateCloses(string envelope)
    {
        using var stream = Frame(envelope);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExchangeHandshake.ReadAsync(stream, "welcome", default));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void Welcome_RequiresMetadataAndABoundedNegotiatedKeepalive()
    {
        using var absent = JsonDocument.Parse("{\"schemaVersion\":2,\"type\":\"welcome\",\"keepaliveSeconds\":30}");
        Assert.Throws<InvalidDataException>(() => ExchangeHandshake.ReadInterval(absent.RootElement));
        using var invalidInterval = JsonSerializer.SerializeToDocument(new
        {
            account = "test", publicEndpoint = "localhost:1234", serverVersion = "fixture",
            keepaliveSeconds = 121, features = Array.Empty<string>()
        });
        Assert.Throws<InvalidDataException>(() => ExchangeHandshake.ReadInterval(invalidInterval.RootElement));
    }

    private static MemoryStream Frame(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        stream.Write(header);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [InlineData("{\"type\":\"\\ud800\"}")]
    [InlineData("{\"type\":\"a\\udc00b\"}")]
    public void String_ReadsALoneSurrogateAsAbsentInsteadOfThrowing(string json)
    {
        using var document = JsonDocument.Parse(json);

        var actual = ExchangeProtocol.String(document.RootElement, "type");

        Assert.Null(actual); // expected: null | actual: <actual>
    }

    [Fact]
    public void String_StillReadsAnOrdinaryMember()
    {
        using var document = JsonDocument.Parse("{\"type\":\"hello\",\"n\":1}");

        Assert.Equal("hello", ExchangeProtocol.String(document.RootElement, "type"));
        Assert.Null(ExchangeProtocol.String(document.RootElement, "n"));
        Assert.Null(ExchangeProtocol.String(document.RootElement, "missing"));
    }
}
