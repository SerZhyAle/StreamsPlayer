using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// `DEVICE-EXCHANGE` item DM: the tunnel of a sealed broadcast runs TLS inside the server's TLS, from the listener's
/// loopback forwarder to the producer. The producer's certificate is self-signed on a P-256 key made for one broadcast;
/// the listener accepts exactly the leaf whose `SHA256:` pin it was given - no name, no chain, no other leaf - and
/// anything else ends the connection with `sealed-failed`.
/// </summary>
public static class ExchangeTunnelTls
{
    /// <summary>The name the listener presents; the pin is the whole check and the name is never compared.</summary>
    public const string InnerHost = "fmsx-tunnel";

    /// <summary>Whether <paramref name="certificate"/> is the leaf <paramref name="pin"/> names (compared in constant time).</summary>
    public static bool Matches(X509Certificate? certificate, string? pin) =>
        certificate is not null && pin is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ExchangeProtocol.Fingerprint(certificate.GetRawCertData())), Encoding.ASCII.GetBytes(pin));

    /// <summary>
    /// The listener's TLS client over the exchange stream, accepting only the pinned leaf. TLS 1.2 is the floor
    /// (item AX). A different leaf, or a handshake that fails, is <see cref="ExchangeBroadcastCertificateException"/>
    /// with the reason `sealed-failed`; nothing is read from or written to the producer past that point.
    /// </summary>
    public static async Task<SslStream> ConnectAsync(Stream exchange, string pin, CancellationToken cancellationToken)
    {
        var tls = new SslStream(exchange, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = InnerHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, pin)
            }, cancellationToken).ConfigureAwait(false);
            return tls;
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw new ExchangeBroadcastCertificateException(ExchangeSealedState.SealedFailed.Code(), exception);
        }
    }
}

/// <summary>
/// The self-signed certificate of one sealed broadcast's inner tunnel TLS (`DEVICE-EXCHANGE` item DM): a P-256 key made
/// for this broadcast and used for nothing else, valid for as long as the broadcast could run, and its `SHA256:` pin -
/// the value that travels in the broadcast's box and in an outward link. Disposing it discards the key.
/// </summary>
public sealed class ExchangeTunnelCertificate : IDisposable
{
    private ExchangeTunnelCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Pin = ExchangeProtocol.Fingerprint(certificate.RawData);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>`SHA256:` of the leaf, spelled as `certFingerprint` is.</summary>
    public string Pin { get; }

    public static ExchangeTunnelCertificate Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + ExchangeTunnelTls.InnerHost, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        // Windows Schannel cannot use the ephemeral key CreateSelfSigned returns; the round trip gives it one it can.
        return new ExchangeTunnelCertificate(
            X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet));
    }

    /// <summary>
    /// The producer's TLS server over one attached tunnel stream, before it bridges to its own local service. TLS 1.2
    /// is the floor; no client certificate is asked for.
    /// </summary>
    public async Task<SslStream> AcceptAsync(Stream attached, CancellationToken cancellationToken)
    {
        var tls = new SslStream(attached, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => Certificate.Dispose();
}
