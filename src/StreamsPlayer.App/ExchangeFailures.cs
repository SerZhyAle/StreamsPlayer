using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0225: turns what went wrong with a connection into the row of the family's table that says so
/// (<c>DEVICE-EXCHANGE</c> item CZ), so a status line stops saying "unavailable" for everything. A refusal never
/// comes through here - a refusal is the server's answer and has its own row (<see cref="ExchangeFailureRows.OfWire"/>).
/// </summary>
internal static class ExchangeFailures
{
    /// <summary>
    /// A connection that failed. <paramref name="connectionOpen"/> is true once the protected connection was up and
    /// this product's own envelope was being written or awaited: a stream that closed with no frame after it is "the
    /// server did not answer", never an unreachable host (item AN).
    /// </summary>
    internal static ExchangeFailureRow Of(Exception exception, bool connectionOpen)
    {
        if (exception is ExchangeCertificateException)
        {
            return ExchangeFailureRows.CertificateChanged;
        }

        if (exception is ExchangeTlsFailedException)
        {
            return ExchangeFailureRows.TlsFailed;
        }

        return connectionOpen ? ExchangeFailureRows.Unanswered : ExchangeFailureRows.Unreachable;
    }

    internal static string KeyOf(Exception exception, bool connectionOpen) => Of(exception, connectionOpen).MessageKey;

    /// <summary>The status key of a certificate the entry did not expect: a first approval, or a server that changed.</summary>
    internal static string CertificateKey(ExchangeCertificateException exception) =>
        exception.Previous is null ? "ExchangeCertificate" : ExchangeFailureRows.CertificateChanged.MessageKey;
}
