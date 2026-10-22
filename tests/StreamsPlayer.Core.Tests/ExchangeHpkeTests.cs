using System.Security.Cryptography;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// DEVICE-EXCHANGE item DH asks an implementation to pass the Auth-mode vectors of RFC 9180 Appendix A.3
/// before it seals anything. This suite holds that line for the one suite the contract names, and pins the
/// vendored corpus by hash.
/// </summary>
public sealed class ExchangeHpkeTests
{
    private static JsonElement Rfc { get; } = E2eCorpus.Load("rfc9180-a3.json").RootElement.Clone();

    private static byte[] Hex(string name) => Convert.FromHexString(Rfc.GetProperty(name).GetString()!);

    private static ExchangeE2eKeyPair Pair(string privateName, string publicName) =>
        ExchangeE2eKeyPair.TryImport(Hex(privateName), Hex(publicName))
        ?? throw new InvalidOperationException(privateName + " does not match " + publicName);

    [Fact]
    public void CorpusFiles_HaveTheirRecordedHashes()
    {
        Assert.Equal("5391a9ae33450c5de82b622278a5acb38773bdf2819ebe8ff8604114758fde35",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(E2eCorpus.Directory, "e2e-v1.json")))));
        Assert.Equal("72dc7bc137566eb7f312ae0784c24f5a19be34f427a1d97bb7e244478f52855d",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(E2eCorpus.Directory, "rfc9180-a3.json")))));
    }

    [Fact]
    public void Rfc9180_A33_SuiteIsTheOneTheContractNames()
    {
        Assert.Equal(2, Rfc.GetProperty("mode").GetInt32());
        Assert.Equal(0x0010, Rfc.GetProperty("kem_id").GetInt32());
        Assert.Equal(0x0001, Rfc.GetProperty("kdf_id").GetInt32());
        Assert.Equal(0x0001, Rfc.GetProperty("aead_id").GetInt32());
    }

    [Fact]
    public void Rfc9180_A33_ReproducesTheKeySchedule()
    {
        using var recipient = Pair("skRm", "pkRm");
        using var sender = Pair("skSm", "pkSm");
        using var ephemeral = Pair("skEm", "pkEm");

        var schedule = ExchangeHpke.AuthEncapSchedule(Hex("pkRm"), sender, ephemeral, Hex("info"));

        Assert.Equal(Hex("shared_secret"), schedule.SharedSecret);
        Assert.Equal(Hex("key_schedule_context"), schedule.KeyScheduleContext);
        Assert.Equal(Hex("secret"), schedule.Secret);
        Assert.Equal(Hex("key"), schedule.Key);
        Assert.Equal(Hex("base_nonce"), schedule.BaseNonce);

        var decapsulated = ExchangeHpke.AuthDecapSchedule(Hex("enc"), recipient, Hex("pkSm"), Hex("info"));
        Assert.Equal(schedule.SharedSecret, decapsulated.SharedSecret);
        Assert.Equal(schedule.Key, decapsulated.Key);
    }

    [Fact]
    public void Rfc9180_A33_SealsTheFirstMessageByteForByte()
    {
        using var sender = Pair("skSm", "pkSm");
        using var ephemeral = Pair("skEm", "pkEm");

        var (enc, ciphertext) = ExchangeHpke.SealAuth(Hex("pkRm"), sender, ephemeral, Hex("info"), Hex("aad"), Hex("pt"));

        Assert.Equal(Hex("enc"), enc);
        Assert.Equal(Hex("ct"), ciphertext);
    }

    [Fact]
    public void Rfc9180_A33_OpensTheFirstMessage()
    {
        using var recipient = Pair("skRm", "pkRm");

        var plaintext = ExchangeHpke.OpenAuth(Hex("enc"), recipient, Hex("pkSm"), Hex("info"), Hex("aad"), Hex("ct"));

        Assert.Equal(Hex("pt"), plaintext);
    }

    [Fact]
    public void Open_RefusesAnotherSenderAnotherAadAndATamperedTag()
    {
        using var recipient = Pair("skRm", "pkRm");
        using var stranger = ExchangeE2eKeyPair.Generate();
        var ciphertext = Hex("ct");

        Assert.ThrowsAny<CryptographicException>(() =>
            ExchangeHpke.OpenAuth(Hex("enc"), recipient, stranger.PublicPoint, Hex("info"), Hex("aad"), ciphertext));
        Assert.ThrowsAny<CryptographicException>(() =>
            ExchangeHpke.OpenAuth(Hex("enc"), recipient, Hex("pkSm"), Hex("info"), [0x00], ciphertext));
        var tampered = (byte[])ciphertext.Clone();
        tampered[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() =>
            ExchangeHpke.OpenAuth(Hex("enc"), recipient, Hex("pkSm"), Hex("info"), Hex("aad"), tampered));
        Assert.ThrowsAny<CryptographicException>(() =>
            ExchangeHpke.OpenAuth(Hex("enc"), recipient, Hex("pkSm"), Hex("info"), Hex("aad"), ciphertext[..8]));
    }

    [Fact]
    public void SealAuth_WithAFreshEphemeralKey_RoundTripsAndNeverRepeatsEnc()
    {
        using var sender = ExchangeE2eKeyPair.Generate();
        using var recipient = ExchangeE2eKeyPair.Generate();
        var info = "fmsx-e2e-v1"u8.ToArray();
        var aad = "broadcast:x"u8.ToArray();
        var message = "secret"u8.ToArray();

        var first = ExchangeHpke.SealAuth(recipient.PublicPoint, sender, info, aad, message);
        var second = ExchangeHpke.SealAuth(recipient.PublicPoint, sender, info, aad, message);

        Assert.Equal(ExchangeHpke.EncLength, first.Enc.Length);
        Assert.NotEqual(first.Enc, second.Enc);
        Assert.Equal(message, ExchangeHpke.OpenAuth(first.Enc, recipient, sender.PublicPoint, info, aad, first.Ciphertext));
    }

    [Fact]
    public void KeyPair_RefusesAPublicPointThatIsNotTheScalarsOwn()
    {
        using var one = ExchangeE2eKeyPair.Generate();
        using var other = ExchangeE2eKeyPair.Generate();

        Assert.Null(ExchangeE2eKeyPair.TryImport(one.PrivateScalar, other.PublicPoint));
        Assert.Null(ExchangeE2eKeyPair.TryImport(one.PrivateScalar[..31], one.PublicPoint));
        Assert.Null(ExchangeE2eKeyPair.TryImport(one.PrivateScalar, one.PublicPoint[1..]));
        using var again = ExchangeE2eKeyPair.TryImport(one.PrivateScalar, one.PublicPoint);
        Assert.NotNull(again);
        Assert.Equal(one.Kem, again.Kem);
    }

    [Fact]
    public void Corpus_DevicesImportAndTheirCheckCodesMatch()
    {
        using var corpus = E2eCorpus.Load();
        var devices = E2eCorpus.Devices(corpus.RootElement);

        Assert.Equal(5, devices.Count);
        foreach (var device in devices.Values)
        {
            Assert.Equal(device.CheckCode, ExchangeE2eKeyPair.CheckCode(device.Pair.PublicPoint));
            Assert.Equal(65, device.Pair.PublicPoint.Length);
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("AQID", true)]
    [InlineData("AQI", true)]
    [InlineData("AQ", true)]
    [InlineData("_-8", true)]
    [InlineData("AQ==", false)]
    [InlineData("A", false)]
    [InlineData("AQ+/", false)]
    [InlineData("AQ ID", false)]
    [InlineData("AR", false)]
    public void Base64Url_IsStrictAndUnpadded(string text, bool valid)
    {
        Assert.Equal(valid, ExchangeBase64Url.TryDecode(text, out var bytes));
        if (valid)
        {
            Assert.Equal(text, ExchangeBase64Url.Encode(bytes));
        }
    }

    [Fact]
    public void Base64Url_NullIsRefused() => Assert.False(ExchangeBase64Url.TryDecode(null, out _));
}
