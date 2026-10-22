using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// DEVICE-EXCHANGE item DH: the sealed box, replayed against the corpus of `vectors/e2e-v1.json` - every
/// box opens to its payload for every recipient, every failing case fails for the reason the corpus names -
/// and round-tripped for what the corpus cannot fix (the sender's random draws).
/// </summary>
public sealed class ExchangeSealedBoxTests
{
    private static readonly JsonDocument Corpus = E2eCorpus.Load();
    private static readonly IReadOnlyDictionary<string, E2eCorpus.Device> Devices = E2eCorpus.Devices(Corpus.RootElement);

    private static ExchangeSealedBox Box(JsonElement element) =>
        ExchangeSealedBox.TryRead(element) ?? throw new InvalidOperationException("The corpus box does not read.");

    public static IEnumerable<object[]> BoxCases()
    {
        foreach (var box in Corpus.RootElement.GetProperty("boxes").EnumerateArray())
        {
            foreach (var recipient in box.GetProperty("to").EnumerateArray())
            {
                yield return [box.GetProperty("name").GetString()!, recipient.GetString()!];
            }
        }
    }

    [Theory]
    [MemberData(nameof(BoxCases))]
    public void Corpus_BoxOpensToItsPayloadForEveryRecipient(string boxName, string recipientName)
    {
        var entry = Corpus.RootElement.GetProperty("boxes").EnumerateArray()
            .Single(box => box.GetProperty("name").GetString() == boxName);
        var sender = Devices[entry.GetProperty("from").GetString()!];
        var reader = Devices[recipientName];

        var opened = ExchangeSealedBoxes.Open(Box(entry.GetProperty("box")), entry.GetProperty("context").GetString()!,
            reader.DeviceId, reader.Pair, sender.Pair.PublicPoint);

        Assert.True(opened.Opened, boxName + " for " + recipientName + ": " + opened.Failure);
        using var payload = JsonDocument.Parse(opened.Payload!);
        Assert.True(E2eCorpus.JsonEquals(entry.GetProperty("payload"), payload.RootElement));
    }

    public static IEnumerable<object[]> FailingCases() =>
        Corpus.RootElement.GetProperty("failing").EnumerateArray().Select(item => new object[] { item.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(FailingCases))]
    public void Corpus_FailingCaseFailsForTheNamedReason(string name)
    {
        var item = Corpus.RootElement.GetProperty("failing").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var source = Corpus.RootElement.GetProperty("boxes").EnumerateArray()
            .Single(box => box.GetProperty("name").GetString() == item.GetProperty("box").GetString());
        var boxElement = item.TryGetProperty("replacedBox", out var replaced) ? replaced : source.GetProperty("box");
        var reader = Devices[item.GetProperty("openAs").GetString()!];
        var trustedForPhone = Devices[item.GetProperty("readerTrustsForPhone").GetString()!];

        var opened = ExchangeSealedBoxes.Open(Box(boxElement), item.GetProperty("context").GetString()!,
            reader.DeviceId, reader.Pair, trustedForPhone.Pair.PublicPoint);

        Assert.False(opened.Opened);
        Assert.Null(opened.Payload);
        Assert.Equal(item.GetProperty("expected").GetString() switch
        {
            "NO_ENTRY" => ExchangeSealedFailure.NoEntry,
            "UNTRUSTED_SENDER" => ExchangeSealedFailure.UntrustedSender,
            "BAD_TAG" => ExchangeSealedFailure.BadTag,
            var other => throw new InvalidOperationException("Unknown expectation " + other)
        }, opened.Failure);
    }

    private static (ExchangeSealedBox Box, byte[] Payload) SealToTabletAndWatch(string context)
    {
        var phone = Devices["phone"];
        var payload = Encoding.UTF8.GetBytes("""{"relayKey":"k","tunnelPin":"SHA256:p"}""");
        var box = ExchangeSealedBoxes.Seal(payload, context, phone.DeviceId, phone.Pair,
            [new(Devices["tablet"].DeviceId, Devices["tablet"].Pair.PublicPoint), new(Devices["watch"].DeviceId, Devices["watch"].Pair.PublicPoint)]);
        return (box, payload);
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("watch")]
    public void Seal_ThenOpen_ReturnsThePayloadToEachRecipient(string reader)
    {
        var (box, payload) = SealToTabletAndWatch("broadcast:b1");
        var device = Devices[reader];

        var opened = ExchangeSealedBoxes.Open(box, "broadcast:b1", device.DeviceId, device.Pair, Devices["phone"].Pair.PublicPoint);

        Assert.Equal(payload, opened.Payload);
    }

    [Fact]
    public void Seal_WritesTheShapeOfItemDh()
    {
        var (box, _) = SealToTabletAndWatch("broadcast:b1");
        var json = JsonSerializer.Serialize(box, ExchangeProtocol.JsonOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("dev-phone", root.GetProperty("from").GetString());
        Assert.Equal(Devices["phone"].Pair.Kem, root.GetProperty("fromKey").GetString());
        Assert.True(ExchangeBase64Url.TryDecode(root.GetProperty("n").GetString(), out var nonce));
        Assert.Equal(12, nonce.Length);
        Assert.Equal(2, root.GetProperty("keys").GetArrayLength());
        foreach (var key in root.GetProperty("keys").EnumerateArray())
        {
            Assert.True(ExchangeBase64Url.TryDecode(key.GetProperty("enc").GetString(), out var enc));
            Assert.Equal(65, enc.Length);
            Assert.True(ExchangeBase64Url.TryDecode(key.GetProperty("wk").GetString(), out var wk));
            Assert.Equal(48, wk.Length);
        }

        Assert.True(ExchangeSealedBox.TryRead(root) is { V: 1 });
    }

    [Fact]
    public void Open_UnderAnotherContextFails()
    {
        var (box, _) = SealToTabletAndWatch("broadcast:b1");
        var tablet = Devices["tablet"];

        var opened = ExchangeSealedBoxes.Open(box, "broadcast:b2", tablet.DeviceId, tablet.Pair, Devices["phone"].Pair.PublicPoint);

        Assert.Equal(ExchangeSealedFailure.BadTag, opened.Failure);
    }

    [Fact]
    public void Open_ByADeviceTheBoxDoesNotNameOrOfAnUntrustedSenderOrAnUnknownVersionFails()
    {
        var (box, _) = SealToTabletAndWatch("broadcast:b1");
        var guest = Devices["guest"];
        var tablet = Devices["tablet"];

        Assert.Equal(ExchangeSealedFailure.NoEntry,
            ExchangeSealedBoxes.Open(box, "broadcast:b1", guest.DeviceId, guest.Pair, Devices["phone"].Pair.PublicPoint).Failure);
        Assert.Equal(ExchangeSealedFailure.UntrustedSender,
            ExchangeSealedBoxes.Open(box, "broadcast:b1", tablet.DeviceId, tablet.Pair, Devices["stranger"].Pair.PublicPoint).Failure);
        Assert.Equal(ExchangeSealedFailure.UntrustedSender,
            ExchangeSealedBoxes.Open(box, "broadcast:b1", tablet.DeviceId, tablet.Pair, ReadOnlySpan<byte>.Empty).Failure);
        Assert.Equal(ExchangeSealedFailure.Malformed,
            ExchangeSealedBoxes.Open(box with { V = 2 }, "broadcast:b1", tablet.DeviceId, tablet.Pair, Devices["phone"].Pair.PublicPoint).Failure);
    }

    [Fact]
    public void Open_AMovedEntryOrATamperedCiphertextOpensNothing()
    {
        var (box, _) = SealToTabletAndWatch("broadcast:b1");
        var tablet = Devices["tablet"];
        var phoneKey = Devices["phone"].Pair.PublicPoint;
        ExchangeBase64Url.TryDecode(box.Ct, out var ct);
        ct[0] ^= 0x01;

        var tampered = ExchangeSealedBoxes.Open(box with { Ct = ExchangeBase64Url.Encode(ct) }, "broadcast:b1", tablet.DeviceId, tablet.Pair, phoneKey);
        var swapped = ExchangeSealedBoxes.Open(
            box with { Keys = [box.Keys[0] with { To = tablet.DeviceId, Wk = box.Keys[1].Wk }, box.Keys[1]] },
            "broadcast:b1", tablet.DeviceId, tablet.Pair, phoneKey);

        Assert.Equal(ExchangeSealedFailure.BadTag, tampered.Failure);
        Assert.Equal(ExchangeSealedFailure.BadTag, swapped.Failure);
        Assert.Null(tampered.Payload);
        Assert.Null(swapped.Payload);
    }

    [Fact]
    public void Seal_AddressesAtMostFortyRecipients()
    {
        var phone = Devices["phone"];
        var recipients = Enumerable.Range(0, 45)
            .Select(index => new ExchangeSealedRecipient("device-" + index, Devices["tablet"].Pair.PublicPoint))
            .ToArray();

        var box = ExchangeSealedBoxes.Seal("{}"u8, "clipboard", phone.DeviceId, phone.Pair, recipients);

        Assert.Equal(40, box.Keys.Count);
        Assert.Equal("device-0", box.Keys[0].To);
        Assert.Equal("device-39", box.Keys[^1].To);
    }

    [Theory]
    [InlineData("""{"v":1,"from":"a","fromKey":"x","n":"y","ct":"z"}""")]
    [InlineData("""{"v":"1","from":"a","fromKey":"x","n":"y","ct":"z","keys":[]}""")]
    [InlineData("""{"v":1,"from":"","fromKey":"x","n":"y","ct":"z","keys":[]}""")]
    [InlineData("""{"v":1,"from":"a","fromKey":"x","n":"y","ct":"z","keys":[{"to":"b","enc":"e"}]}""")]
    [InlineData("""[1]""")]
    public void TryRead_RefusesAMalformedBox(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(ExchangeSealedBox.TryRead(document.RootElement));
    }

    [Fact]
    public void TryRead_IgnoresUnknownMembers()
    {
        using var document = JsonDocument.Parse("""{"v":1,"from":"a","fromKey":"x","n":"y","ct":"z","keys":[],"future":{"a":1}}""");

        Assert.NotNull(ExchangeSealedBox.TryRead(document.RootElement));
    }
}
