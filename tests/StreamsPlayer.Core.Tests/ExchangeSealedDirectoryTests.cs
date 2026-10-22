using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// DEVICE-EXCHANGE section 27 as the directory carries it: `e2eKey` and `capabilities` on a device (item DF), `seal`
/// on the endpoints and a `sealed` box on a broadcast record (item DK) - read from the frames a real server
/// (FMS_Win's, `vectors/fmsw-234/`) sent, and from records built here for the cases the corpus has no frame for.
/// </summary>
public sealed class ExchangeSealedDirectoryTests
{
    private static readonly JsonDocument Frames = JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vectors", "fmsw-234", "frames-v2.json")));
    private static readonly JsonDocument Corpus = E2eCorpus.Load();
    private static readonly IReadOnlyDictionary<string, E2eCorpus.Device> Devices = E2eCorpus.Devices(Corpus.RootElement);

    private static IEnumerable<JsonDocument> ServerFrames(string type)
    {
        foreach (var transcript in Frames.RootElement.GetProperty("transcripts").EnumerateArray()
                     .Where(t => t.GetProperty("name").GetString()!.StartsWith("sealed", StringComparison.Ordinal)))
        {
            foreach (var step in transcript.GetProperty("steps").EnumerateArray()
                         .Where(s => s.TryGetProperty("direction", out var d) && d.GetString() == "server->client"))
            {
                // A step that carries raw bytes (a chunk, a clipboard value) has `hex` and no `json`.
                if (!step.TryGetProperty("json", out var jsonElement) || jsonElement.ValueKind != JsonValueKind.String
                    || jsonElement.GetString() is not { } json || !json.StartsWith('{'))
                {
                    continue;
                }

                var frame = JsonDocument.Parse(json);
                if (frame.RootElement.TryGetProperty("type", out var t) && t.GetString() == type)
                {
                    yield return frame;
                }
                else
                {
                    frame.Dispose();
                }
            }
        }
    }

    private static ExchangeDirectorySnapshot Apply(JsonDocument frame)
    {
        var state = new ExchangeDirectoryState();
        return state.ApplyFull(frame.RootElement).Snapshot;
    }

    [Fact]
    public void RealServerFrames_DeviceKeysAndCapabilitiesRead()
    {
        var withKey = 0;
        var without = 0;
        foreach (var frame in ServerFrames("directory"))
        {
            var snapshot = Apply(frame);
            foreach (var device in frame.RootElement.GetProperty("devices").EnumerateArray())
            {
                var record = snapshot.Devices!.Single(d => d.DeviceId == device.GetProperty("deviceId").GetString());
                if (device.TryGetProperty("e2eKey", out var key))
                {
                    withKey++;
                    Assert.True(ExchangeBase64Url.TryDecode(key.GetProperty("kem").GetString(), out var point));
                    Assert.Equal(point, record.E2eKey);
                    Assert.True(record.HasE2eCapability);
                }
                else
                {
                    without++;
                    Assert.Null(record.E2eKey);
                }
            }
        }

        Assert.True(withKey > 0, "the corpus must carry keyed devices");
        Assert.True(without > 0, "the corpus must carry a device that predates the capability");
    }

    // The server transcripts carry a stand-in descriptor (endpoints only, no title or top-level address) because the
    // server is content-agnostic, so the whole-record reader rightly skips it. The members this product adds are read
    // from the real frames at member level here, and through the whole reader in the synthetic records below.
    private static IEnumerable<JsonElement> BroadcastRecords(JsonElement frame)
    {
        if (frame.TryGetProperty("broadcasts", out var full) && full.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in full.EnumerateArray())
            {
                yield return record;
            }
        }

        if (frame.TryGetProperty("upserts", out var upserts) && upserts.TryGetProperty("broadcasts", out var pushed)
            && pushed.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in pushed.EnumerateArray())
            {
                yield return record;
            }
        }
    }

    [Fact]
    public void RealServerFrames_ASealedBroadcastRecordsSealAndBoxReadAndTheBoxOpensForAnAddressedDevice()
    {
        var cases = Frames.RootElement.GetProperty("sealed").GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("context").GetString()!.StartsWith("broadcast:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(cases);
        var opened = 0;

        foreach (var type in new[] { "directory", "changed" })
        {
            foreach (var frame in ServerFrames(type))
            {
                foreach (var record in BroadcastRecords(frame.RootElement).Where(r => r.TryGetProperty("sealed", out _)))
                {
                    var box = ExchangeSealedBox.TryRead(record.GetProperty("sealed"));
                    Assert.NotNull(box);
                    var broadcastId = record.GetProperty("broadcastId").GetString()!;
                    var context = ExchangeSealedContext.Broadcast(broadcastId);

                    // Each exchange endpoint declares the seal version and carries no key; the box carries the keys.
                    foreach (var endpoint in record.GetProperty("descriptor").GetProperty("endpoints").EnumerateArray()
                                 .Where(e => e.GetProperty("transport").GetString() is "RELAY" or "TUNNEL"))
                    {
                        Assert.True(ExchangeEndpointSeal.Read(endpoint) is { IsKnown: true, Key: null, Pin: null });
                    }

                    // A record carries the box current at its revision (a device joining or leaving replaces it), so
                    // the case is the one whose box is this one.
                    var match = cases.FirstOrDefault(c => c.GetProperty("context").GetString() == context
                        && E2eCorpus.JsonEquals(c.GetProperty("box"), record.GetProperty("sealed")));
                    if (match.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var sender = Devices[match.GetProperty("from").GetProperty("device").GetString()!];
                    foreach (var recipient in match.GetProperty("to").EnumerateArray())
                    {
                        var reader = Devices[recipient.GetProperty("device").GetString()!];
                        var result = ExchangeSealedBoxes.Open(box, context, recipient.GetProperty("deviceId").GetString()!,
                            reader.Pair, sender.Pair.PublicPoint);
                        Assert.True(result.Opened, result.Failure.ToString());
                        using var payload = JsonDocument.Parse(result.Payload!);
                        Assert.True(E2eCorpus.JsonEquals(match.GetProperty("payload"), payload.RootElement));
                        opened++;
                    }
                }
            }
        }

        Assert.True(opened > 0, "at least one real sealed broadcast record must have been opened");
    }
    private static ExchangeDirectorySnapshot Snapshot(string descriptorJson, string? extraRecordMembers = null)
    {
        var frame = $$"""
            {"type":"directory","revision":1,"devices":[],"broadcasts":[
              {"broadcastId":"b1","deviceId":"d1","mode":"AUDIO_ONLY","title":"t","descriptor":{{descriptorJson}}{{extraRecordMembers}}}]}
            """;
        using var document = JsonDocument.Parse(frame);
        return new ExchangeDirectoryState().ApplyFull(document.RootElement).Snapshot;
    }

    private static string Descriptor(string endpointsJson, string url) => $$"""
        {"schemaVersion":1,"url":"{{url}}","mode":"AUDIO_ONLY","title":"t","isLive":true,"endpoints":[{{endpointsJson}}]}
        """;

    private const string RelayUrl = "https://exchange.example.net:22022/v2/b/b1/stream";
    private const string TunnelUrl = "fmsx://exchange.example.net:22022/b/b1/http";
    private const string Lan = """{"url":"http://192.168.1.5:8554/live-audio.aac","transport":"HTTP","mode":"AUDIO_ONLY"}""";

    private static string Relay(string seal = "") =>
        $$"""{"url":"{{RelayUrl}}","transport":"RELAY","mode":"AUDIO_ONLY"{{seal}}}""";

    private static string Tunnel(string seal = "") =>
        $$"""{"url":"{{TunnelUrl}}","transport":"TUNNEL","mode":"AUDIO_ONLY","inner":"http://192.0.2.1:8554/live-audio.aac"{{seal}}}""";

    [Fact]
    public void SealedExchangeEndpoints_ArePlayableAttempts()
    {
        var view = Snapshot(Descriptor(Relay(""","seal":{"v":1}""") + "," + Tunnel(""","seal":{"v":1}"""), RelayUrl))
            .Groups.Single().Broadcasts.Single();

        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
        Assert.Equal(["RELAY", "TUNNEL"], view.Record.Descriptor!.PlaybackAttemptEndpoints().Select(e => e.Transport).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(""","seal":{"v":2}""")]
    [InlineData(""","seal":{}""")]
    [InlineData(""","seal":"yes" """)]
    [InlineData(""","seal":{"v":"1"}""")]
    public void AnExchangeEndpointWithoutAKnownSealIsNeverAnAttemptAndTheStateIsShown(string seal)
    {
        var view = Snapshot(Descriptor(Relay(seal) + "," + Tunnel(seal), RelayUrl)).Groups.Single().Broadcasts.Single();

        Assert.Equal(ExchangeBroadcastSupport.Unsealed, view.Support);
        Assert.Empty(view.Record.Descriptor!.PlaybackAttemptEndpoints());
        Assert.All(view.Record.Descriptor.Endpoints, endpoint => Assert.True(ExchangeEndpointKinds.IsRefusedAsUnsealed(endpoint)));
    }

    [Fact]
    public void ALanEndpointStaysPlayableBesideAnUnsealedRelay()
    {
        var view = Snapshot(Descriptor(Lan + "," + Relay(), "http://192.168.1.5:8554/live-audio.aac")).Groups.Single().Broadcasts.Single();

        Assert.Equal(ExchangeBroadcastSupport.Supported, view.Support);
        var attempts = view.Record.Descriptor!.PlaybackAttemptEndpoints();
        Assert.Equal(["HTTP"], attempts.Select(e => e.Transport).ToArray());
        Assert.False(ExchangeEndpointKinds.IsRefusedAsUnsealed(attempts.Single()));
    }

    [Fact]
    public void TheTopLevelAddressOfARelayOnlyDescriptorIsNotAClearFallback()
    {
        var withList = Snapshot(Descriptor(Relay(), RelayUrl)).Groups.Single().Broadcasts.Single();
        var noList = Snapshot(Descriptor("", RelayUrl)).Groups.Single().Broadcasts.Single();

        Assert.Equal(ExchangeBroadcastSupport.Unsealed, withList.Support);
        Assert.Equal(ExchangeBroadcastSupport.Unsealed, noList.Support);
        Assert.Empty(noList.Record.Descriptor!.PlaybackAttemptEndpoints());
        Assert.True(ExchangeEndpointKinds.IsExchangeAddress(RelayUrl));
        Assert.True(ExchangeEndpointKinds.IsExchangeAddress(TunnelUrl));
        Assert.False(ExchangeEndpointKinds.IsExchangeAddress("https://example.net/radio/stream"));
        Assert.False(ExchangeEndpointKinds.IsExchangeAddress("http://192.168.1.5:8554/live-audio.aac"));
    }

    [Fact]
    public void ATunnelOnlyDescriptorWithoutASealIsRefusedToo()
    {
        // A descriptor's top-level address must itself be launchable to be read at all, so the relay stands there.
        var view = Snapshot(Descriptor(Tunnel(), RelayUrl)).Groups.Single().Broadcasts.Single();

        Assert.Equal(ExchangeBroadcastSupport.Unsealed, view.Support);
        Assert.Empty(view.Record.Descriptor!.PlaybackAttemptEndpoints());
    }

    [Fact]
    public void ARecordWithASealedMemberKeepsTheBoxUnopenedAndAMalformedOneIsFlagged()
    {
        const string good = ""","sealed":{"v":1,"from":"d1","fromKey":"x","n":"y","ct":"z","keys":[]}""";
        const string bad = ""","sealed":"nope" """;
        var withBox = Snapshot(Descriptor(Relay(""","seal":{"v":1}"""), RelayUrl), good).Groups.Single().Broadcasts.Single().Record;
        var withBad = Snapshot(Descriptor(Relay(""","seal":{"v":1}"""), RelayUrl), bad).Groups.Single().Broadcasts.Single().Record;
        var without = Snapshot(Descriptor(Relay(""","seal":{"v":1}"""), RelayUrl)).Groups.Single().Broadcasts.Single().Record;

        Assert.NotNull(withBox.Sealed);
        Assert.False(withBox.SealedUnreadable);
        Assert.Null(withBad.Sealed);
        Assert.True(withBad.SealedUnreadable);
        Assert.Null(without.Sealed);
        Assert.False(without.SealedUnreadable);
    }

    [Fact]
    public void AnEndpointSealPrintsNoKeyAndPersistsNoKey()
    {
        var seal = new ExchangeEndpointSeal(1, Key: "SECRET-KEY-MATERIAL", Pin: "SHA256:SECRET-PIN");
        var endpoint = new FastMediaSorterBroadcastEndpoint(RelayUrl, "RELAY", "AUDIO_ONLY", null, null, null, null, true, 2000, null, null, seal);

        Assert.DoesNotContain("SECRET", seal.ToString());
        Assert.DoesNotContain("SECRET", endpoint.ToString());
        var json = JsonSerializer.Serialize(endpoint);
        Assert.DoesNotContain("SECRET", json);
        Assert.Contains("\"V\":1", json);
        var restored = JsonSerializer.Deserialize<FastMediaSorterBroadcastEndpoint>(json)!;
        Assert.True(restored.Seal is { IsKnown: true, Key: null, Pin: null });
    }

    [Fact]
    public void AnOutwardDescriptorCarriesTheKeysInItsEndpointsAndTheReaderKeepsThem()
    {
        var key = ExchangeBase64Url.Encode(new byte[32]);
        var descriptor = Descriptor(Relay($$""","seal":{"v":1,"key":"{{key}}"}""") + "," + Tunnel(""","seal":{"v":1,"pin":"SHA256:abc"}"""), RelayUrl);

        var read = FastMediaSorterBroadcastDescriptor.Read(System.Text.Encoding.UTF8.GetBytes(descriptor));

        Assert.True(read.IsAccepted);
        var endpoints = read.Broadcast!.Endpoints;
        Assert.Equal(new byte[32], endpoints.Single(e => e.Transport == "RELAY").Seal!.GroupKey());
        Assert.Equal("SHA256:abc", endpoints.Single(e => e.Transport == "TUNNEL").Seal!.Pin);
    }
}
