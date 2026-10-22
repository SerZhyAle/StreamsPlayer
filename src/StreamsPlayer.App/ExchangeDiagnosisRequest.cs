using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0225: what a person asked the connection check to look at - which failure the screen was showing, and
/// which server. Built from the keys the screens already hold; nothing is measured here.
/// </summary>
internal sealed record ExchangeDiagnosisRequest(ExchangeFailureRow Row, string HappenedKey, string Host, int Port,
    bool Connected, string? Subject = null)
{
    private const string OnlineKey = "ExchangeOnline";
    private const string FirstApprovalKey = "ExchangeCertificate";

    /// <summary>
    /// The request for the account's connection as the screens show it. A failure line (the last attempt's outcome
    /// first, then the connection line) is the row to walk; a connection that is up needs no check; anything else -
    /// off, still connecting, an endpoint the person typed that is not the account's - is the plain question "why
    /// won't it connect", walked from the first step. A certificate waiting for its first approval keeps its own
    /// line and takes the certificate step's advice.
    /// </summary>
    internal static ExchangeDiagnosisRequest ForStatus(string? outcomeKey, string statusKey, string host, int port,
        bool sameAsAccount = true)
    {
        if (sameAsAccount)
        {
            var key = ExchangeFailureRows.ByMessageKey(outcomeKey) is not null || outcomeKey == FirstApprovalKey
                ? outcomeKey! : statusKey;
            if (key == FirstApprovalKey)
            {
                return new ExchangeDiagnosisRequest(ExchangeFailureRows.CertificateChanged, FirstApprovalKey, host, port, false);
            }

            if (ExchangeFailureRows.ByMessageKey(key) is { } row)
            {
                return new ExchangeDiagnosisRequest(row, row.MessageKey, host, port, false);
            }

            if (key == OnlineKey)
            {
                return new ExchangeDiagnosisRequest(ExchangeFailureRows.Unreachable, OnlineKey, host, port, true);
            }
        }

        return new ExchangeDiagnosisRequest(ExchangeFailureRows.Unreachable, ExchangeFailureRows.Unreachable.MessageKey,
            host, port, false);
    }

    /// <summary>The address as a person writes it, behind the details control only (requirement 4).</summary>
    internal string Endpoint => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
