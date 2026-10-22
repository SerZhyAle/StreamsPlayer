using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0203 requirement 4: the one certificate decision every relay connection installs. A descriptor
/// that states no <c>certFingerprint</c> is validated by the platform trust store; one that does
/// pins the leaf instead - SHA-256 of the DER leaf in the contract's <c>SHA256:</c> plus unpadded
/// base64 form (<see cref="ExchangeProtocol.Fingerprint"/>). A pin that does not match is a refusal
/// and the connection ends there: no caller retries with checking off, not as a setting and not as
/// a fallback (DEVICE-EXCHANGE item V). The playback engines cannot pin (the SP-0203 engine spike),
/// which is why the video path plays such endpoints through <see cref="ExchangeRelayProxy"/> - the
/// proxy is where this decision runs; the audio route installs it in its own HTTP handler.
/// </summary>
public static class ExchangeCertificatePin
{
    /// <summary>
    /// Item AX: TLS 1.2 at least, 1.3 where both ends have it. Every client of the family states this rather than
    /// taking the operating system's default, which on an older Windows still offers what the contract forbids.
    /// </summary>
    public const System.Security.Authentication.SslProtocols ClientProtocols =
        System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;

    /// <summary>
    /// The single TLS certificate callback: <paramref name="certificate"/> is the leaf the peer
    /// presented, <paramref name="chainErrors"/> the platform's verdict on the chain. Pin present,
    /// the equality is the whole decision; pin absent, only a clean platform chain is accepted.
    /// </summary>
    public static bool Accepts(X509Certificate? certificate, string? pin, SslPolicyErrors chainErrors)
    {
        if (certificate is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pin))
        {
            return string.Equals(pin, ExchangeProtocol.Fingerprint(certificate.GetRawCertData()), StringComparison.Ordinal);
        }

        return chainErrors == SslPolicyErrors.None;
    }

    /// <summary>
    /// Item AY, for an account entry's pin only: the leaf is accepted when it is the pinned one, or when it
    /// carries the pinned public key - a renewal with the same key needs no new confirmation, because the
    /// device holds no password to re-prove anything (item D). A leaf with another key is refused whatever
    /// its dates, and an entry that holds no pin accepts nothing (the first acceptance is the user's).
    /// A descriptor's <c>certFingerprint</c> goes through <see cref="Accepts"/>: it lives as long as its
    /// broadcast and is never carried over to a renewal.
    /// </summary>
    public static bool AcceptsAccountLeaf(X509Certificate? certificate, string? pin, string? pinKey)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(pin))
        {
            return false;
        }

        var raw = certificate.GetRawCertData();
        if (string.Equals(pin, ExchangeProtocol.Fingerprint(raw), StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(pinKey)
            && string.Equals(pinKey, ExchangeProtocol.PublicKeyFingerprint(raw), StringComparison.Ordinal);
    }
}
