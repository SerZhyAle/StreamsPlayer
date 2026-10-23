using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace StreamsPlayer.Core;

public sealed record BroadcastCertificateLeaf(string Fingerprint, bool PlatformTrusted);

/// <summary>Descriptor TLS trust, independent of account enrollment. A probe sends no application bytes or credentials.</summary>
public static class BroadcastCertificateTrust
{
    public static string? EndpointIdentity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "fmsx"))
        {
            return null;
        }

        var port = uri.Port > 0 ? uri.Port : ExchangeTunnelUrl.DefaultExchangePort;
        return uri.Scheme + "://" + uri.IdnHost.ToLowerInvariant() + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<BroadcastCertificateLeaf?> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (EndpointIdentity(url) is null)
        {
            return null;
        }

        var uri = new Uri(url);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : ExchangeTunnelUrl.DefaultExchangePort, cancellationToken).ConfigureAwait(false);
        BroadcastCertificateLeaf? leaf = null;
        using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, errors) =>
        {
            if (certificate is null)
            {
                return false;
            }

            leaf = new BroadcastCertificateLeaf(ExchangeProtocol.Fingerprint(certificate.GetRawCertData()), errors == SslPolicyErrors.None);
            return leaf.PlatformTrusted;
        });
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.Host, EnabledSslProtocols = ExchangeCertificatePin.ClientProtocols
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException) when (leaf is { PlatformTrusted: false })
        {
            // An untrusted leaf is returned for a prompt, never accepted by this probe.
        }

        return leaf;
    }
}
