using System.Text.Json;
using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0217's producer wire, without a network: the cast answer of `DEVICE-EXCHANGE` 7.8 maps each of
/// its four reasons to its own message, the tunnel `open` parses, a receiver's declared pairs gate a
/// cast offer, and the extended redaction list masks a broadcast cycle's secrets.
/// </summary>
public sealed class ExchangeProducerWireTests
{
    [Theory]
    [InlineData("declined", false, "BroadcastCastDeclined")]
    [InlineData("timeout", false, "BroadcastCastTimeout")]
    [InlineData("unsupported", false, "BroadcastCastUnsupported")]
    [InlineData("unavailable", false, "BroadcastCastUnavailable")]
    [InlineData("some-future-reason", false, "BroadcastCastUnavailable")]
    public void EachCastReasonMapsToItsOwnMessage(string reason, bool accepted, string expected)
    {
        var root = JsonDocument.Parse($$"""
            {"schemaVersion":2,"type":"cast-result","broadcastId":"b1","receiverDeviceId":"r1","accepted":{{(accepted ? "true" : "false")}},"reason":"{{reason}}"}
            """);
        var result = ExchangeCastResult.TryParse(root.RootElement);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.MessageKey);
    }

    [Fact]
    public void AnAcceptedCastHasItsOwnMessage()
    {
        var root = JsonDocument.Parse("""{"schemaVersion":2,"type":"cast-result","broadcastId":"b1","receiverDeviceId":"r1","accepted":true}""");
        var result = ExchangeCastResult.TryParse(root.RootElement);
        Assert.NotNull(result);
        Assert.True(result!.Accepted);
        Assert.Equal("BroadcastCastAccepted", result.MessageKey);
    }

    [Fact]
    public void NotACastResultOrAnUnanswerableOneIsRefused()
    {
        var other = JsonDocument.Parse("""{"schemaVersion":2,"type":"directory"}""");
        Assert.Null(ExchangeCastResult.TryParse(other.RootElement));

        var noId = JsonDocument.Parse("""{"schemaVersion":2,"type":"cast-result","accepted":true}""");
        Assert.Null(ExchangeCastResult.TryParse(noId.RootElement));
    }

    [Fact]
    public void ATunnelOpenParses()
    {
        var root = JsonDocument.Parse("""{"schemaVersion":2,"type":"open","tunnelId":"t1","broadcastId":"b1","scheme":"http"}""");
        var open = ExchangeTunnelOpen.TryParse(root.RootElement);
        Assert.NotNull(open);
        Assert.Equal("t1", open!.TunnelId);
        Assert.Equal("b1", open.BroadcastId);
        Assert.Equal("http", open.Scheme);
        Assert.Equal(16, ExchangeTunnelOpen.TunnelsPerBroadcast);
    }

    [Fact]
    public void AReceiverThatCoversAnEndpointIsOffered_OneThatDoesNotIsNot()
    {
        var endpoints = new[]
        {
            new FastMediaSorterBroadcastEndpoint("https://example.test:44022/v2/b/b1/stream", "RELAY", "AUDIO_ONLY",
                null, "aac", 44100, 128000, true, 2000),
            new FastMediaSorterBroadcastEndpoint("http://192.168.1.5:8768/live-audio.aac", "HTTP", "AUDIO_ONLY",
                null, "aac", 44100, 128000, true, 500)
        };

        var audioOverRelay = new[] { new ExchangePlayPair("AUDIO_ONLY", "RELAY") };
        var audioOverLan = new[] { new ExchangePlayPair("AUDIO_ONLY", "HTTP") };
        var videoOnly = new[] { new ExchangePlayPair("VIDEO_ONLY", "RELAY") };

        Assert.True(ExchangeCastEligibility.CanPlay(audioOverRelay, endpoints));
        Assert.True(ExchangeCastEligibility.CanPlay(audioOverLan, endpoints));
        Assert.False(ExchangeCastEligibility.CanPlay(videoOnly, endpoints));
    }

    [Fact]
    public void ADeviceThatDeclaredNoUsableReceiverIsNeverOfferedACast()
    {
        var endpoints = new[]
        {
            new FastMediaSorterBroadcastEndpoint("https://example.test:44022/v2/b/b1/stream", "RELAY", "AUDIO_ONLY",
                null, "aac", 44100, 128000, true, 2000)
        };
        var covers = new[] { new ExchangePlayPair("AUDIO_ONLY", "RELAY") };

        Assert.False(ExchangeCastEligibility.CanOffer(null, endpoints));
        Assert.False(ExchangeCastEligibility.CanOffer(null, []));
        Assert.True(ExchangeCastEligibility.CanOffer(covers, endpoints));
        Assert.False(ExchangeCastEligibility.CanOffer([new ExchangePlayPair("VIDEO_ONLY", "RELAY")], endpoints));
    }

    [Theory]
    [InlineData("""{"deviceId":"d1","deviceName":"Phone","presence":"online"}""")]
    [InlineData("""{"deviceId":"d1","deviceName":"Phone","presence":"online","receiver":{}}""")]
    [InlineData("""{"deviceId":"d1","deviceName":"Phone","presence":"online","receiver":{"modes":[],"transports":[]}}""")]
    public void ADeviceWithNoUsableReceiverDeclarationHasNoPairsToOfferAgainst(string device)
    {
        var directory = JsonDocument.Parse("""{"schemaVersion":2,"type":"directory","revision":1,"devices":[""" + device + """],"broadcasts":[]}""");

        var transition = new ExchangeDirectoryState().ApplyFull(directory.RootElement);

        Assert.Null(transition.Snapshot.Devices!.Single().Plays);
    }

    [Fact]
    public void TheDirectoryReadsTheReceiverPairsADeviceDeclared()
    {
        var state = new ExchangeDirectoryState();
        var directory = JsonDocument.Parse("""
        {
          "schemaVersion": 2, "type": "directory", "revision": 1,
          "devices": [
            {"deviceId": "d-plays", "deviceName": "Phone", "presence": "online",
             "receiver": {"plays": [{"mode": "AUDIO_ONLY", "transport": "RELAY"}]}},
            {"deviceId": "d-legacy", "deviceName": "Watch", "presence": "online",
             "receiver": {"modes": ["AUDIO_ONLY"], "transports": ["HTTP", "RELAY"]}},
            {"deviceId": "d-not-receiver", "deviceName": "Server", "presence": "offline"}
          ],
          "broadcasts": []
        }
        """);
        var transition = state.ApplyFull(directory.RootElement);
        var plays = transition.Snapshot.Devices!.Single(device => device.DeviceId == "d-plays").Plays;
        Assert.NotNull(plays);
        Assert.Contains(new ExchangePlayPair("AUDIO_ONLY", "RELAY"), plays);

        var legacy = transition.Snapshot.Devices!.Single(device => device.DeviceId == "d-legacy").Plays;
        Assert.NotNull(legacy);
        Assert.Equal(2, legacy!.Count);

        Assert.Null(transition.Snapshot.Devices!.Single(device => device.DeviceId == "d-not-receiver").Plays);
    }
}

/// <summary>
/// SP-0217's redaction tests: after a full broadcast cycle the log and the diagnostic bundle never
/// carry a broadcast id, the opaque path segment, a token, or the description's addresses.
/// </summary>
public sealed class BroadcastRedactionTests
{
    [Theory]
    [InlineData("started broadcastId=abc123DEF on http://192.168.1.5:8768/live-audio.aac")]
    [InlineData("relay url https://example.test:44022/v2/b/abc123DEF/stream unreachable")]
    [InlineData("tunnel fmsx://example.test:44022/b/abc123DEF/http inner=http://192.0.2.1:8768/live-audio.aac")]
    [InlineData("share FMSBCAST1:H4sIAAAAAAAAA0jNycnRVCC1AKK2UgAA link refused")]
    [InlineData("fmsbcast://import?payload=FMSBCAST1%3AH4sIAAAAAAAA copied")]
    public void ABroadcastAddressOrPayloadIsMaskedWhole(string line)
    {
        var redacted = ExchangeDiagnosticRedactor.Redact(line);
        Assert.DoesNotContain("abc123DEF", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.5", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("live-audio", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("H4sIAAAAAAAA", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOpaqueVideoSegmentIsMaskedWithItsAddress()
    {
        var redacted = ExchangeDiagnosticRedactor.Redact("serving http://192.168.1.5:8768/opaque-token-9/live.ts");
        Assert.DoesNotContain("opaque-token-9", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryStationLineIsLeftAlone()
    {
        const string line = "playing http://radio.example.net/live.mp3 for user";
        Assert.Equal(line, ExchangeDiagnosticRedactor.Redact(line));
    }
}
