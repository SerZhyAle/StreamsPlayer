using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0221 phase 1: what the account's sign-in reads and writes without a network - the invitation key `i`
/// (item AR), the display name's bidirectional controls (item BO), the account kind (item AO), the
/// `password-change` frame (item AT), the key pin of a renewal (item AY) and the new refusal vocabulary.
/// </summary>
public sealed class ExchangeAccountSignInTests
{
    private const string Invitation = "a3gPp-pvQPdQN7qwDDVbyQ";

    [Fact]
    public void Invitation_IsValidatedAndOnlyItsPresenceIsKept()
    {
        var result = ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test%3A80&u=carol&i=" + Invitation);
        Assert.True(result.Success, result.Reason);
        Assert.True(result.Data!.HasInvitation);
        Assert.Equal("carol", result.Data.Login);
        // A string read from this product's own entry never carries the code (item AE): Encode has no member for it.
        Assert.DoesNotContain("i=", ExchangeConnectionStringCodec.Encode(result.Data));
        Assert.False(ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test%3A80").Data!.HasInvitation);
    }

    [Theory]
    [InlineData("i=")]
    [InlineData("i=a3gPp-pvQPdQN7qwDDVby")]
    [InlineData("i=a3gPp-pvQPdQN7qwDDVbyQA")]
    [InlineData("i=a3gPp-pvQPdQN7qwDDVbyR")]
    [InlineData("i=a3gPp%2BpvQPdQN7qwDDVbyQ")]
    public void MalformedInvitation_RefusesTheWholeString(string field)
    {
        var result = ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test%3A80&" + field);
        Assert.Null(result.Data);
        Assert.Equal("bad-i", result.Reason);
    }

    [Fact]
    public void RepeatedInvitation_RefusesTheWholeString()
    {
        var result = ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test%3A80&i=" + Invitation + "&i=" + Invitation);
        Assert.Equal("repeated-key", result.Reason);
    }

    [Theory]
    [InlineData("%D8%9C")]
    [InlineData("%E2%80%8E")]
    [InlineData("%E2%80%8F")]
    [InlineData("%E2%80%AA")]
    [InlineData("%E2%80%AE")]
    [InlineData("%E2%81%A6")]
    [InlineData("%E2%81%A9")]
    public void NameWithABidirectionalControl_IsRefused(string encoded)
    {
        var result = ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test%3A80&n=Office" + encoded + "evil");
        Assert.Equal("bad-n", result.Reason);
    }

    [Fact]
    public void NameWithAZeroWidthJoiner_IsKept()
    {
        // U+200D joins the emoji of a family sequence; refusing it would refuse names in several scripts.
        var result = ExchangeConnectionStringCodec.Parse("FMSEXCH1:x=a.test%3A80&n=a%E2%80%8Db");
        Assert.True(result.Success, result.Reason);
        Assert.Equal("a‍b", result.Data!.Name);
    }

    [Theory]
    [InlineData(null, ExchangeAccountKind.Member)]
    [InlineData("member", ExchangeAccountKind.Member)]
    [InlineData("guest", ExchangeAccountKind.Guest)]
    [InlineData("", ExchangeAccountKind.Guest)]
    [InlineData("family", ExchangeAccountKind.Guest)]
    [InlineData("Member", ExchangeAccountKind.Guest)]
    public void AccountKind_AbsentIsMemberAndUnknownIsTheRestrictiveReading(string? value, ExchangeAccountKind expected) =>
        Assert.Equal(expected, ExchangeAccountKinds.Parse(value));

    [Fact]
    public void PasswordChange_WritesTheContractFrameFromArraysAndClearsThem()
    {
        var change = new ExchangePasswordChange("Passw0rdTest".ToCharArray(), "Vector-new-pw-2".ToCharArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes<object>(change, ExchangeProtocol.JsonOptions);
        Assert.Equal("{\"schemaVersion\":2,\"type\":\"password-change\",\"oldPassword\":\"Passw0rdTest\",\"newPassword\":\"Vector-new-pw-2\"}",
            Encoding.UTF8.GetString(bytes));
        change.Clear();
        Assert.All(change.Old, character => Assert.Equal('\0', character));
        Assert.All(change.New, character => Assert.Equal('\0', character));
    }

    [Fact]
    public void PasswordChangeTypes_AreKnownSoAnUnexpectedOneIsNotSilentlyIgnored()
    {
        Assert.True(ExchangeProtocol.IsKnownType("password-change"));
        Assert.True(ExchangeProtocol.IsKnownType("password-changed"));
    }

    [Theory]
    [InlineData("signed-out", "ExchangeSignedOut")]
    [InlineData("password-policy", "ExchangePasswordPolicy")]
    [InlineData("limit-reached", "ExchangeLimitReached")]
    [InlineData("not-permitted", "ExchangeNotPermitted")]
    [InlineData("a-reason-from-the-future", "ExchangeRefused")]
    public void RefusalKey_NamesTheAccountReasons(string reason, string key) =>
        Assert.Equal(key, ExchangeProtocol.RefusalKey(reason));

    private static X509Certificate2 Leaf(ECDsa key, string commonName) => X509CertificateLoader.LoadCertificate(
        new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(30)).RawData);

    [Fact]
    public void RenewalWithTheSameKey_IsAcceptedAndAnotherKeyIsNot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var first = Leaf(key, "server");
        using var renewed = Leaf(key, "server");
        using var foreign = Leaf(other, "server");
        var pin = ExchangeProtocol.Fingerprint(first.RawData);
        var pinKey = ExchangeProtocol.PublicKeyFingerprint(first.RawData);
        Assert.NotNull(pinKey);
        Assert.NotEqual(ExchangeProtocol.Fingerprint(renewed.RawData), pin);
        Assert.Equal(pinKey, ExchangeProtocol.PublicKeyFingerprint(renewed.RawData));

        Assert.True(ExchangeCertificatePin.AcceptsAccountLeaf(first, pin, pinKey));
        Assert.True(ExchangeCertificatePin.AcceptsAccountLeaf(renewed, pin, pinKey));
        Assert.False(ExchangeCertificatePin.AcceptsAccountLeaf(foreign, pin, pinKey));
        // An entry from before the key was kept still pins by leaf alone: a renewal is a changed certificate.
        Assert.False(ExchangeCertificatePin.AcceptsAccountLeaf(renewed, pin, null));
        Assert.False(ExchangeCertificatePin.AcceptsAccountLeaf(first, null, pinKey));
        Assert.False(ExchangeCertificatePin.AcceptsAccountLeaf(null, pin, pinKey));
    }

    [Fact]
    public void DescriptorPin_NeverFollowsARenewal()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var first = Leaf(key, "server");
        using var renewed = Leaf(key, "server");
        Assert.False(ExchangeCertificatePin.Accepts(renewed, ExchangeProtocol.Fingerprint(first.RawData),
            SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void PublicKeyFingerprint_OfNonsenseIsNull() =>
        Assert.Null(ExchangeProtocol.PublicKeyFingerprint([1, 2, 3]));

    [Theory]
    [InlineData("{\"oldPassword\":\"Hunter2-old\"}")]
    [InlineData("{\"newPassword\":\"Hunter2-new\"}")]
    [InlineData("{\"invitationPassword\":\"shared-secret\"}")]
    [InlineData("invitation=a3gPp-pvQPdQN7qwDDVbyQ")]
    public void Diagnostics_NeverCarryAPasswordOrAnInvitation(string text)
    {
        var redacted = ExchangeDiagnosticRedactor.Redact("sent " + text + " ok");
        Assert.DoesNotContain("Hunter2", redacted);
        Assert.DoesNotContain("shared-secret", redacted);
        Assert.DoesNotContain("a3gPp", redacted);
    }
}
