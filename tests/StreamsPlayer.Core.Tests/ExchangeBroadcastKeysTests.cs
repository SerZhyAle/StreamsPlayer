using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0226: the keys of a sealed broadcast, from the box a directory record carries (item DK), from a guest's grant
/// (item DJ) and from a link's endpoints (`LIVE-BROADCAST` item Q), and the one decision every playback leg takes
/// from them (item DP) - replayed with the corpus's boxes, so it is the real bytes that open.
/// </summary>
public sealed class ExchangeBroadcastKeysTests
{
    private static readonly JsonDocument Corpus = E2eCorpus.Load();
    private static readonly IReadOnlyDictionary<string, E2eCorpus.Device> Devices = E2eCorpus.Devices(Corpus.RootElement);
    private static readonly JsonDocument Frames = JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vectors", "fmsw-234", "frames-v2.json")));

    private static JsonElement BroadcastBox => Corpus.RootElement.GetProperty("boxes").EnumerateArray()
        .Single(box => box.GetProperty("name").GetString() == "broadcast");

    private static string BroadcastId => BroadcastBox.GetProperty("context").GetString()!["broadcast:".Length..];

    private static ExchangeBroadcastRecord Record(JsonElement box, string? grantId = null) => new(
        BroadcastId, "dev-phone", "t", "AUDIO_ONLY", null, Sealed: ExchangeSealedBox.TryRead(box), GrantId: grantId);

    private static ExchangeDeviceTrust TrustingPhone()
    {
        var trust = new ExchangeDeviceTrust();
        trust.ObserveDevice("dev-phone", Devices["phone"].Pair.PublicPoint);
        return trust;
    }

    [Fact]
    public void ARecordBoxOpensWithTheTabletsKey_AndHandsOverTheRelayKeyThePinAndThePair()
    {
        var tablet = Devices["tablet"];

        var state = ExchangeBroadcastKeys.OpenRecord(Record(BroadcastBox.GetProperty("box")), tablet.DeviceId, tablet.Pair, TrustingPhone());

        Assert.Equal(ExchangeSealedState.None, state);
        var held = ExchangeBroadcastKeys.Find(BroadcastId)!;
        Assert.True(ExchangeBase64Url.TryDecode(BroadcastBox.GetProperty("payload").GetProperty("relayKey").GetString(), out var relayKey));
        Assert.Equal(relayKey, held.RelayKey);
        Assert.Equal(BroadcastBox.GetProperty("payload").GetProperty("tunnelPin").GetString(), held.TunnelPin);
        // The corpus's stand-in payload spells the user `username`; item BF spells it `user`. Both read.
        Assert.Equal(new ExchangeRelayCredentials("listener", "broadcast-password"), held.Credentials);
        Assert.DoesNotContain("broadcast-password", held.ToString());
    }

    [Fact]
    public void ABoxFromASenderNobodyListedOpensNothing_AndFromAChangedKeySaysKeyChanged()
    {
        var tablet = Devices["tablet"];
        var box = Record(BroadcastBox.GetProperty("box"));

        Assert.Equal(ExchangeSealedState.SealedFailed,
            ExchangeBroadcastKeys.OpenRecord(box, tablet.DeviceId, tablet.Pair, new ExchangeDeviceTrust()));
        // A failed open also forgets what an earlier box of this broadcast had handed over.
        Assert.Null(ExchangeBroadcastKeys.Find(BroadcastId));

        var changed = new ExchangeDeviceTrust();
        changed.ObserveDevice("dev-phone", Devices["stranger"].Pair.PublicPoint);
        Assert.Equal(ExchangeSealedState.KeyChanged,
            ExchangeBroadcastKeys.OpenRecord(box, tablet.DeviceId, tablet.Pair, changed));
        Assert.Equal(ExchangeSealedState.KeyChanged, ExchangeBroadcastKeys.FailureOf(BroadcastId));
    }

    [Fact]
    public void ABoxThatAddressesAnotherDeviceFailsWithoutALeak()
    {
        var guest = Devices["guest"];

        var state = ExchangeBroadcastKeys.OpenRecord(Record(BroadcastBox.GetProperty("box")), guest.DeviceId, guest.Pair, TrustingPhone());

        Assert.Equal(ExchangeSealedState.SealedFailed, state);
        Assert.Equal(ExchangeSealedState.SealedFailed, ExchangeBroadcastKeys.FailureOf(BroadcastId));
    }

    [Fact]
    public void ARecordWithoutABoxIsNotAFailure_AndAnUnreadableBoxIs()
    {
        var tablet = Devices["tablet"];
        var plain = new ExchangeBroadcastRecord("plain-1", "d", "t", "AUDIO_ONLY", null);
        var unreadable = plain with { BroadcastId = "plain-2", SealedUnreadable = true };

        Assert.Equal(ExchangeSealedState.None, ExchangeBroadcastKeys.OpenRecord(plain, tablet.DeviceId, tablet.Pair, new ExchangeDeviceTrust()));
        Assert.Equal(ExchangeSealedState.SealedFailed, ExchangeBroadcastKeys.OpenRecord(unreadable, tablet.DeviceId, tablet.Pair, new ExchangeDeviceTrust()));
    }

    [Fact]
    public void AGuestsGrantBoxFixesTheSendersKeyOnFirstUse_AndAnotherKeyLaterIsKeyChanged()
    {
        var cases = Frames.RootElement.GetProperty("sealed").GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("context").GetString()!.StartsWith("grant:", StringComparison.Ordinal)
                && c.GetProperty("payload").TryGetProperty("relayKey", out _)).ToArray();
        Assert.NotEmpty(cases);
        var grantCase = cases[0];
        var grantId = grantCase.GetProperty("context").GetString()!["grant:".Length..];
        var recipient = grantCase.GetProperty("to").EnumerateArray().First();
        var guest = Devices[recipient.GetProperty("device").GetString()!];
        var record = new ExchangeBroadcastRecord("granted-1", "owner-device", "t", "AUDIO_ONLY", null,
            Sealed: ExchangeSealedBox.TryRead(grantCase.GetProperty("box")), GrantId: grantId);
        var trust = new ExchangeDeviceTrust();

        var first = ExchangeBroadcastKeys.OpenRecord(record, recipient.GetProperty("deviceId").GetString()!, guest.Pair, trust);

        Assert.Equal(ExchangeSealedState.None, first);
        Assert.NotNull(trust.TrustedGrant(grantId));
        Assert.True(ExchangeBase64Url.TryDecode(grantCase.GetProperty("payload").GetProperty("relayKey").GetString(), out var relayKey));
        Assert.Equal(relayKey, ExchangeBroadcastKeys.Find("granted-1")!.RelayKey);

        // The same grant from another sender key is never opened.
        trust.ForgetGrant(grantId);
        trust.ObserveGrant(grantId, Devices["stranger"].Pair.PublicPoint);
        var second = ExchangeBroadcastKeys.OpenRecord(record, recipient.GetProperty("deviceId").GetString()!, guest.Pair, trust);
        Assert.Equal(ExchangeSealedState.KeyChanged, second);
    }

    // ------------------------------------------------------------------ the decision

    private static FastMediaSorterBroadcastEndpoint Endpoint(string url, string transport, ExchangeEndpointSeal? seal, string? inner = null) =>
        new(url, transport, "AUDIO_ONLY", null, null, null, null, true, 2000, null, inner, seal);

    private const string RelayUrl = "https://relay.example.net:44022/v2/b/decide-1/stream";
    private const string TunnelUrl = "fmsx://relay.example.net:44022/b/decide-1/http";

    [Fact]
    public void ALanEndpointIsNotJudged_AnUnsealedExchangeEndpointIsRefused()
    {
        var lan = Endpoint("http://192.168.1.5:8554/live-audio.aac", "HTTP", null);

        Assert.True(ExchangeBroadcastKeys.Decide(lan) is { Allowed: true, IsSealed: false });
        Assert.True(ExchangeBroadcastKeys.Decide(Endpoint(RelayUrl, "RELAY", null)) is { Allowed: false, State: ExchangeSealedState.E2eMissing });
        Assert.True(ExchangeBroadcastKeys.Decide(Endpoint(TunnelUrl, "TUNNEL", null, "http://192.0.2.1:8554/x")) is { Allowed: false });
        // Judged by the address, not by what the descriptor says the transport is.
        Assert.False(ExchangeBroadcastKeys.Decide(Endpoint(RelayUrl, "HTTP", null)).Allowed);
        Assert.False(ExchangeBroadcastKeys.Decide(Endpoint(RelayUrl, "RELAY", new ExchangeEndpointSeal(2))).Allowed);
    }

    [Fact]
    public void ASealedRelayNeedsItsKey_FromTheBoxOrFromTheLinkItself()
    {
        var sealedEndpoint = Endpoint(RelayUrl, "RELAY", new ExchangeEndpointSeal(1));

        Assert.True(ExchangeBroadcastKeys.Decide(sealedEndpoint) is { Allowed: false, State: ExchangeSealedState.SealedFailed });

        var key = new byte[32];
        key[0] = 7;
        ExchangeBroadcastKeys.Put("decide-1", new ExchangeBroadcastSecrets(key, null, new ExchangeRelayCredentials("u", "p")));
        var decision = ExchangeBroadcastKeys.Decide(sealedEndpoint);
        Assert.True(decision is { Allowed: true, IsSealed: true });
        Assert.Equal(key, decision.RelayKey);
        Assert.Equal(new ExchangeRelayCredentials("u", "p"), decision.Credentials);
        Assert.Equal("decide-1", decision.RelayOptions(RelayUrl)!.BroadcastId);

        // A link's own key wins over what the directory held.
        var linkKey = new byte[32];
        linkKey[0] = 9;
        var withLink = Endpoint(RelayUrl, "RELAY", new ExchangeEndpointSeal(1, Key: ExchangeBase64Url.Encode(linkKey)));
        Assert.Equal(linkKey, ExchangeBroadcastKeys.Decide(withLink).RelayKey);
        ExchangeBroadcastKeys.Clear();
    }

    [Fact]
    public void ASealedTunnelNeedsItsPin()
    {
        ExchangeBroadcastKeys.Clear();
        var tunnel = Endpoint(TunnelUrl, "TUNNEL", new ExchangeEndpointSeal(1), "http://192.0.2.1:8554/x");

        Assert.False(ExchangeBroadcastKeys.Decide(tunnel).Allowed);
        ExchangeBroadcastKeys.Put("decide-1", new ExchangeBroadcastSecrets(new byte[32], "SHA256:" + new string('A', 43), null));
        var decision = ExchangeBroadcastKeys.Decide(tunnel);
        Assert.True(decision.Allowed);
        Assert.Equal("SHA256:" + new string('A', 43), decision.TunnelPin);
        Assert.Null(decision.RelayOptions(TunnelUrl));
        ExchangeBroadcastKeys.Clear();
    }

    [Fact]
    public void ARememberedLink_IsHeldAndHandedToTheSecretStoreAndMergesTheTwoEndpoints()
    {
        ExchangeBroadcastKeys.Clear();
        var key = new byte[32];
        key[5] = 5;
        var saved = new Dictionary<string, ExchangeBroadcastSecrets>();
        var endpoints = new[]
        {
            Endpoint(RelayUrl, "RELAY", new ExchangeEndpointSeal(1, Key: ExchangeBase64Url.Encode(key))),
            Endpoint(TunnelUrl, "TUNNEL", new ExchangeEndpointSeal(1, Pin: "SHA256:" + new string('B', 43)), "http://192.0.2.1:8554/x"),
            Endpoint("http://192.168.1.5:8554/live-audio.aac", "HTTP", null)
        };

        ExchangeBroadcastKeys.RememberLink(endpoints, (id, secrets) => saved[id] = secrets);

        var held = ExchangeBroadcastKeys.Find("decide-1")!;
        Assert.Equal(key, held.RelayKey);
        Assert.Equal("SHA256:" + new string('B', 43), held.TunnelPin);
        Assert.Equal(held, saved["decide-1"]);
        Assert.Single(saved);
        ExchangeBroadcastKeys.Clear();
    }

    [Fact]
    public void ThePersistedLookupAnswersWhenNothingIsHeldInMemory()
    {
        ExchangeBroadcastKeys.Clear();
        var key = new byte[32];
        ExchangeBroadcastKeys.PersistedLookup = id => id == "restarted-1" ? new ExchangeBroadcastSecrets(key, null, null) : null;
        try
        {
            Assert.Equal(key, ExchangeBroadcastKeys.Find("restarted-1")!.RelayKey);
            Assert.Null(ExchangeBroadcastKeys.Find("other"));
        }
        finally
        {
            ExchangeBroadcastKeys.PersistedLookup = null;
        }
    }

    [Theory]
    [InlineData("https://relay.example.net:44022/v2/b/abc-123/stream", "abc-123")]
    [InlineData("fmsx://relay.example.net:44022/b/abc-123/http", "abc-123")]
    [InlineData("fmsx://relay.example.net:44022/b/abc-123/rtsp", "abc-123")]
    [InlineData("http://192.168.1.5:8554/live-audio.aac", null)]
    [InlineData("not a url", null)]
    public void TheBroadcastIdIsReadFromTheRelayAndTunnelAddresses(string url, string? expected) =>
        Assert.Equal(expected, ExchangeBroadcastKeys.BroadcastIdOf(url));

    [Fact]
    public void ThePayloadReaderRefusesAMemberThatIsPresentAndWrong()
    {
        Assert.Null(ExchangeBroadcastKeys.ReadPayload("""{"relayKey":"short"}"""u8));
        Assert.Null(ExchangeBroadcastKeys.ReadPayload("""{"tunnelPin":"not-a-pin"}"""u8));
        Assert.Null(ExchangeBroadcastKeys.ReadPayload("[1]"u8));
        Assert.NotNull(ExchangeBroadcastKeys.ReadPayload("{}"u8));
        var json = $$$"""{"relayKey":"{{{ExchangeBase64Url.Encode(new byte[32])}}}","credentials":{"user":"a","password":"b"}}""";
        var good = ExchangeBroadcastKeys.ReadPayload(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(new ExchangeRelayCredentials("a", "b"), good!.Credentials);
    }
}
