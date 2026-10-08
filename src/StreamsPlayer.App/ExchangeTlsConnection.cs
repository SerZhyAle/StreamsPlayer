using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed class ExchangeCertificateException(string? previous, string presented) : AuthenticationException
{
    internal string? Previous { get; } = previous;
    internal string Presented { get; } = presented;
}

internal sealed class ExchangeTlsConnection : IDisposable
{
    private readonly TcpClient _client;
    internal SslStream Stream { get; }
    internal string Fingerprint { get; private set; } = "";

    private ExchangeTlsConnection(TcpClient client, string? pin)
    {
        _client = client;
        Stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            Fingerprint = ExchangeProtocol.Fingerprint(certificate.GetRawCertData());
            // SP-0200 owner policy: even a platform-trusted leaf requires explicit first acceptance.
            return pin is not null && string.Equals(pin, Fingerprint, StringComparison.Ordinal);
        });
    }

    internal static async Task<ExchangeTlsConnection> OpenAsync(string host, int port, string? pin, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        ExchangeTlsConnection? connection = null;
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            connection = new ExchangeTlsConnection(client, pin);
            await connection.Stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None
            }, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (AuthenticationException) when (connection is { Fingerprint.Length: > 0 }
            && !string.Equals(pin, connection.Fingerprint, StringComparison.Ordinal))
        {
            var presented = connection.Fingerprint;
            connection.Dispose();
            throw new ExchangeCertificateException(pin, presented);
        }
        catch
        {
            connection?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Stream.Dispose();
        _client.Dispose();
    }
}
