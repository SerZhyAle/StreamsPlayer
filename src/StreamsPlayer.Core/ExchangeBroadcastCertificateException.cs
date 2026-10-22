using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace StreamsPlayer.Core;

public sealed class ExchangeBroadcastCertificateException(string reason, Exception? inner = null)
    : AuthenticationException(reason, inner)
{
    public const string UntrustedWithoutFingerprint = "certificate-untrusted-no-fingerprint";
    public const string Refused = "certificate-refused";

    public string Reason { get; } = reason;

    // SP-0226: the named reasons of sealed content (`DEVICE-EXCHANGE` item DR) end a leg the same way a refused
    // certificate does - once, in words, with no retry that could mend a key that is missing, changed or wrong -
    // so every leg that already asks this table for its terminal sentence says them too.
    public static string? MessageKey(string? reason) => reason switch
    {
        UntrustedWithoutFingerprint => "BroadcastCertificateUntrusted",
        Refused => "BroadcastCertificateMismatch",
        _ => ExchangeSealedStates.FromCode(reason) is var sealedState and not ExchangeSealedState.None ? sealedState.MessageKey() : null
    };

    internal static SslClientAuthenticationOptions ClientOptions(string host) => new()
    {
        TargetHost = host,
        EnabledSslProtocols = ExchangeCertificatePin.ClientProtocols
    };

    internal static async Task<Stream> ConnectAsync(string host, int port, string? pin, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        SslStream? ssl = null;
        string? refusal = null;
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            ssl = new SslStream(client.GetStream(), false, (_, certificate, _, errors) =>
            {
                var accepts = ExchangeCertificatePin.Accepts(certificate, pin, errors);
                if (!accepts)
                    refusal = string.IsNullOrWhiteSpace(pin) ? UntrustedWithoutFingerprint : Refused;
                return accepts;
            });
            await ssl.AuthenticateAsClientAsync(ClientOptions(host), cancellationToken).ConfigureAwait(false);
            return ssl;
        }
        catch (AuthenticationException exception) when (refusal is not null)
        {
            ssl?.Dispose();
            client.Dispose();
            throw new ExchangeBroadcastCertificateException(refusal, exception);
        }
        catch
        {
            ssl?.Dispose();
            client.Dispose();
            throw;
        }
    }
}
