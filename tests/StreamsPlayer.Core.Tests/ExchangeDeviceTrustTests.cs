using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>DEVICE-EXCHANGE items DG and DJ: the first key seen is the key; a different one is never used.</summary>
public sealed class ExchangeDeviceTrustTests
{
    private static byte[] Point() => ExchangeE2eKeyPair.Generate().PublicPoint;

    [Fact]
    public void FirstSightTrusts_TheSameKeyStaysTrusted_AnotherIsKeyChanged()
    {
        var trust = new ExchangeDeviceTrust();
        var first = Point();
        var other = Point();

        Assert.Equal(ExchangeKeyObservation.NewlyTrusted, trust.ObserveDevice("d1", first));
        Assert.Equal(ExchangeKeyObservation.Trusted, trust.ObserveDevice("d1", first));
        Assert.Equal(ExchangeKeyObservation.KeyChanged, trust.ObserveDevice("d1", other));
        Assert.Equal(ExchangeKeyObservation.KeyChanged, trust.ObserveDevice("d1", other));
        Assert.Equal(first, trust.TrustedDevice("d1"));
    }

    [Fact]
    public void AKeyNeverReplacesTheTrustedOne_OnlyForgettingTheDeviceLetsANewKeyIn()
    {
        var trust = new ExchangeDeviceTrust();
        var first = Point();
        var second = Point();
        trust.ObserveDevice("d1", first);

        Assert.Equal(ExchangeKeyObservation.KeyChanged, trust.ObserveDevice("d1", second));
        Assert.True(trust.ForgetDevice("d1"));
        Assert.False(trust.ForgetDevice("d1"));
        Assert.Equal(ExchangeKeyObservation.NewlyTrusted, trust.ObserveDevice("d1", second));
        Assert.Equal(second, trust.TrustedDevice("d1"));
    }

    [Fact]
    public void DevicesAndGrantsAreSeparateBooks()
    {
        var trust = new ExchangeDeviceTrust();
        var point = Point();

        trust.ObserveDevice("same-id", point);

        Assert.Null(trust.TrustedGrant("same-id"));
        Assert.Equal(ExchangeKeyObservation.NewlyTrusted, trust.ObserveGrant("same-id", Point()));
        Assert.Equal(point, trust.TrustedDevice("same-id"));
        Assert.True(trust.ForgetGrant("same-id"));
        Assert.Null(trust.TrustedGrant("same-id"));
    }

    [Fact]
    public void ARoundTripThroughItsStoredShapeKeepsEveryKeyAndOnlyChangesMarkDirty()
    {
        var trust = new ExchangeDeviceTrust();
        var device = Point();
        var grant = Point();
        trust.ObserveDevice("d1", device);
        trust.ObserveGrant("g1", grant);
        Assert.True(trust.IsDirty);

        var data = trust.ToData();
        Assert.False(trust.IsDirty);
        trust.ObserveDevice("d1", device);
        Assert.False(trust.IsDirty);

        var restored = ExchangeDeviceTrust.FromData(data);
        Assert.Equal(device, restored.TrustedDevice("d1"));
        Assert.Equal(grant, restored.TrustedGrant("g1"));
        Assert.Equal(ExchangeKeyObservation.KeyChanged, restored.ObserveDevice("d1", Point()));
    }

    [Fact]
    public void AStoredEntryThatIsNotAP256PointIsDroppedNeverTrusted()
    {
        var data = new ExchangeDeviceTrustData(
            new() { ["short"] = "AQID", ["notBase64"] = "***", ["good"] = ExchangeBase64Url.Encode(Point()) },
            null);

        var trust = ExchangeDeviceTrust.FromData(data);

        Assert.Null(trust.TrustedDevice("short"));
        Assert.Null(trust.TrustedDevice("notBase64"));
        Assert.NotNull(trust.TrustedDevice("good"));
        Assert.NotNull(ExchangeDeviceTrust.FromData(null));
    }

    private static ExchangeDeviceRecord Device(string id, byte[]? key, bool capability = true, DateTimeOffset? seen = null) =>
        new(id, "name-" + id, true, null, key, capability, seen);

    [Fact]
    public void Classify_SeparatesTheRecipientFromTheKeylessAndTheChanged()
    {
        var trust = new ExchangeDeviceTrust();
        var key = Point();

        Assert.Equal(ExchangeSealedState.E2eMissing, ExchangeRecipients.Classify(Device("legacy", null, capability: false), trust));
        Assert.Equal(ExchangeSealedState.E2eMissing, ExchangeRecipients.Classify(Device("no-key", null), trust));
        Assert.Equal(ExchangeSealedState.E2eMissing, ExchangeRecipients.Classify(Device("no-capability", key, capability: false), trust));
        Assert.Equal(ExchangeSealedState.None, ExchangeRecipients.Classify(Device("ok", key), trust));
        Assert.Equal(ExchangeSealedState.KeyChanged, ExchangeRecipients.Classify(Device("ok", Point()), trust));
    }

    [Fact]
    public void Recipients_AreTheOtherTrustedDevicesMostRecentFirst_UnderTheKeyFirstSeen()
    {
        var trust = new ExchangeDeviceTrust();
        var oldKey = Point();
        var now = DateTimeOffset.UtcNow;
        var devices = new[]
        {
            Device("self", Point(), seen: now),
            Device("older", oldKey, seen: now.AddHours(-2)),
            Device("newer", Point(), seen: now.AddHours(-1)),
            Device("never-seen", Point()),
            Device("keyless", null),
            Device("changed", Point(), seen: now)
        };
        trust.ObserveDevice("changed", Point());

        var recipients = ExchangeRecipients.For(devices, "self", trust);

        Assert.Equal(["newer", "older", "never-seen"], recipients.Select(recipient => recipient.DeviceId).ToArray());
        Assert.Equal(oldKey, recipients.Single(recipient => recipient.DeviceId == "older").PublicPoint);
    }

    [Fact]
    public void SealedStates_HaveTheirContractCodesAndOneMessageKeyEach()
    {
        foreach (var state in new[] { ExchangeSealedState.E2eMissing, ExchangeSealedState.KeyChanged,
                     ExchangeSealedState.ServerNoE2e, ExchangeSealedState.SealedFailed })
        {
            Assert.Equal(state, ExchangeSealedStates.FromCode(state.Code()));
            Assert.StartsWith("ExchangeState", state.MessageKey());
        }

        Assert.Equal(ExchangeSealedState.None, ExchangeSealedStates.FromCode("something-else"));
        Assert.Equal("e2e-missing", ExchangeSealedState.E2eMissing.Code());
        Assert.Equal("key-changed", ExchangeSealedState.KeyChanged.Code());
        Assert.Equal("server-no-e2e", ExchangeSealedState.ServerNoE2e.Code());
        Assert.Equal("sealed-failed", ExchangeSealedState.SealedFailed.Code());
    }
    private static readonly ExchangeSealedState[] SaidStates =
        [ExchangeSealedState.E2eMissing, ExchangeSealedState.KeyChanged, ExchangeSealedState.ServerNoE2e, ExchangeSealedState.SealedFailed];

    [Fact]
    public void EveryStateIsSaidInEveryLanguage_AndTheContractsOwnWordsAreUsedForEnglishRussianAndUkrainian()
    {
        var keys = SaidStates.Select(state => state.MessageKey()).Append("ExchangeE2eOwnKeyMissing").Append("BroadcastsUnsealedReason")
            .Append("ExchangeHonestText").ToArray();
        foreach (var dictionary in LocalizationDictionary.LoadAll())
        {
            foreach (var key in keys)
            {
                Assert.True(dictionary.Values.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text), $"{dictionary.Code}/{key}");
            }
        }

        // Item DR / item DE: the three languages the contract writes are used word for word.
        var english = LocalizationDictionary.LoadAll().Single(dictionary => dictionary.Code == "en").Values;
        Assert.Equal("This device cannot receive protected content yet, so nothing is sent to it through the server.", english["ExchangeStateE2eMissing"]);
        Assert.Equal("This could not be opened on this device. Try again from the other device.", english["ExchangeStateSealedFailed"]);
        Assert.StartsWith("The exchange server connects your devices, so it knows the names of your account, devices, folders and broadcasts,", english["ExchangeHonestText"]);
        Assert.EndsWith("and it would appear in your list of devices.", english["ExchangeHonestText"]);
        var russian = LocalizationDictionary.LoadAll().Single(dictionary => dictionary.Code == "ru").Values;
        Assert.StartsWith("Сервер обмена соединяет ваши устройства, поэтому знает имена вашего аккаунта, устройств, папок и трансляций,", russian["ExchangeHonestText"]);
        var ukrainian = LocalizationDictionary.LoadAll().Single(dictionary => dictionary.Code == "uk").Values;
        Assert.StartsWith("Сервер обміну з'єднує ваші пристрої, тому знає імена вашого акаунта, пристроїв, папок і трансляцій,", ukrainian["ExchangeHonestText"]);
    }

    [Theory]
    [InlineData("e2e-missing", "ExchangeStateE2eMissing")]
    [InlineData("key-changed", "ExchangeStateKeyChanged")]
    [InlineData("server-no-e2e", "ExchangeStateServerNoE2e")]
    [InlineData("sealed-failed", "ExchangeStateSealedFailed")]
    public void ASealedReasonEndsALegOnceInWords_WithNoRetry(string reason, string messageKey)
    {
        // The reason string the playback legs carry maps to the sentence the person reads, through the same table
        // the certificate refusals use, and the incoming-broadcast windows treat it as terminal.
        Assert.Equal(messageKey, ExchangeBroadcastCertificateException.MessageKey(reason));
        Assert.Equal(messageKey, IncomingBroadcastFailure.TerminalMessageKey(reason, null));
    }
}
