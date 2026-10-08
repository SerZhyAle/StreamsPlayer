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
}
