using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0203 requirement 4: the one certificate decision - platform validation when the descriptor
/// states no pin, leaf equality when it does. A mismatched pin is refused and there is no caller
/// that retries with checking off.
/// </summary>
public sealed class ExchangeCertificatePinTests
{
    private static X509Certificate2 SelfSigned(string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        return X509CertificateLoader.LoadPkcs12(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7)).Export(X509ContentType.Pfx), null);
    }

    private static string Fingerprint(X509Certificate2 certificate) =>
        ExchangeProtocol.Fingerprint(certificate.GetRawCertData());

    [Fact]
    public void TheRightPinAcceptsTheLeafItNames()
    {
        using var leaf = SelfSigned("streamsplayer-relay");
        Assert.True(ExchangeCertificatePin.Accepts(leaf, Fingerprint(leaf), SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void TheWrongPinIsRefusedEvenOnAChainError()
    {
        using var leaf = SelfSigned("streamsplayer-relay");
        using var other = SelfSigned("some-other-leaf");
        Assert.False(ExchangeCertificatePin.Accepts(leaf, Fingerprint(other), SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void AChangedCertificateIsRefusedNotSilentlyAccepted()
    {
        using var first = SelfSigned("streamsplayer-relay");
        using var renewed = SelfSigned("streamsplayer-relay");
        Assert.NotEqual(Fingerprint(first), Fingerprint(renewed));
        // The stored pin names the first leaf; the renewed one fails platform validation (self-signed)
        // and does not match the pin - the refusal is the end.
        Assert.False(ExchangeCertificatePin.Accepts(renewed, Fingerprint(first), SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void WithoutAPinOnlyAPlatformCleanChainIsAccepted()
    {
        using var leaf = SelfSigned("streamsplayer-relay");
        Assert.False(ExchangeCertificatePin.Accepts(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(ExchangeCertificatePin.Accepts(leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(ExchangeCertificatePin.Accepts(leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void NoCertificateIsRefusedWhateverThePinSays()
    {
        Assert.False(ExchangeCertificatePin.Accepts(null, "SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk", SslPolicyErrors.None));
        Assert.False(ExchangeCertificatePin.Accepts(null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void APinIsNeverTradedForACheckedOffConnection()
    {
        // The pin wins even when the platform would have refused for a name mismatch: the equality
        // with the pinned leaf is the whole decision, and nothing in the API offers an unchecked path.
        using var leaf = SelfSigned("streamsplayer-relay");
        Assert.True(ExchangeCertificatePin.Accepts(leaf, Fingerprint(leaf), SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(ExchangeCertificatePin.Accepts(SelfSigned("other"), Fingerprint(leaf), SslPolicyErrors.None));
    }
}
