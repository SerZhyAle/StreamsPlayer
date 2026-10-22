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

/// <summary>
/// SP-0225: the secure connection could not be set up although the TCP connection was made - a failure of the
/// protected link itself, not a changed certificate and not a host that cannot be reached. It is an
/// <see cref="AuthenticationException"/> so every existing "connection failed" filter still matches it.
/// </summary>
internal sealed class ExchangeTlsFailedException(Exception inner) : AuthenticationException("The secure connection failed.", inner);

internal sealed class ExchangeTlsConnection : IDisposable
{
    private readonly TcpClient _client;
    internal SslStream Stream { get; }
    internal string Fingerprint { get; private set; } = "";

    /// <summary>The leaf's <c>SHA256:</c> public-key value (item AY), recorded next to the pin once a connection is accepted.</summary>
    internal string? PublicKeyFingerprint { get; private set; }

    /// <summary>Whether the certificate callback accepted the leaf: a refusal for another reason is not a changed certificate.</summary>
    private bool _leafRefused;

    private ExchangeTlsConnection(TcpClient client, string? pin, string? pinKey)
    {
        _client = client;
        Stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            var raw = certificate.GetRawCertData();
            Fingerprint = ExchangeProtocol.Fingerprint(raw);
            PublicKeyFingerprint = ExchangeProtocol.PublicKeyFingerprint(raw);
            // SP-0200 owner policy: even a platform-trusted leaf requires explicit first acceptance. Item AY: a
            // renewal that keeps the pinned key is the same server, so it is accepted with no new confirmation.
            var accepted = ExchangeCertificatePin.AcceptsAccountLeaf(certificate, pin, pinKey);
            _leafRefused = !accepted;
            return accepted;
        });
    }

    /// <summary>
    /// Opens TLS to the endpoint and authenticates the server's leaf against the entry's pin and key. TLS 1.2 is
    /// the floor and 1.3 is taken where both ends have it (item AX): nothing lower is offered, whatever the
    /// operating system's defaults say.
    /// </summary>
    internal static async Task<ExchangeTlsConnection> OpenAsync(string host, int port, string? pin, CancellationToken cancellationToken,
        string? pinKey = null)
    {
        var client = new TcpClient();
        ExchangeTlsConnection? connection = null;
        var connected = false;
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            connected = true;
            connection = new ExchangeTlsConnection(client, pin, pinKey);
            await connection.Stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (AuthenticationException) when (connection is { Fingerprint.Length: > 0, _leafRefused: true })
        {
            var presented = connection.Fingerprint;
            connection.Dispose();
            throw new ExchangeCertificateException(pin, presented);
        }
        catch (Exception exception) when (connected && exception is AuthenticationException or IOException)
        {
            connection?.Dispose();
            client.Dispose();
            throw new ExchangeTlsFailedException(exception);
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
