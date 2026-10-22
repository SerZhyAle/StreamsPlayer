using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// DEVICE-EXCHANGE item AX: TLS 1.2 at least, stated on the connection and not left to the operating system.
/// A handshake that happens to land on 1.3 proves nothing about the floor, so these read the configured set.
/// </summary>
public sealed class ExchangeTlsFloorTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=relay", key, HashAlgorithmName.SHA256);
        return X509CertificateLoader.LoadPkcs12(
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7)).Export(X509ContentType.Pfx), null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk")]
    public void TheRelayAudioClientStatesTls12AndTls13(string? fingerprint)
    {
        var options = FastMediaSorterPlaybackTransport.PinnedSslOptions(fingerprint, _ => { });

        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, options.EnabledSslProtocols);
    }

    [Fact]
    public void TheRelayAudioClientKeepsItsPinDecisionAndNamesTheRefusal()
    {
        using var leaf = SelfSigned();
        var refusals = new List<string>();

        var pinned = FastMediaSorterPlaybackTransport.PinnedSslOptions("SHA256:8f6TQvCbXjDMOyu4A9JzKcWlEHmR5pNsGgVaU2wYqhk", refusals.Add);
        var unpinned = FastMediaSorterPlaybackTransport.PinnedSslOptions(null, refusals.Add);

        Assert.False(pinned.RemoteCertificateValidationCallback!(this, leaf, null, SslPolicyErrors.None));
        Assert.False(unpinned.RemoteCertificateValidationCallback!(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(unpinned.RemoteCertificateValidationCallback!(this, leaf, null, SslPolicyErrors.None));
        Assert.Equal([ExchangeBroadcastCertificateException.Refused, ExchangeBroadcastCertificateException.UntrustedWithoutFingerprint],
            refusals);
    }
}
