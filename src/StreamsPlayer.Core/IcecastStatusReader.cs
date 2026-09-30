using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Polls Icecast's <c>status-json.xsl</c> endpoint for the mount currently being played.
/// </summary>
/// <remarks>
/// Unlike an ICY reader this never consumes media bytes: each bounded status document is a small request
/// to the stream's own origin. A compatible endpoint is polled only while its channel is playing; an
/// absent or malformed endpoint returns promptly so callers can retain their existing metadata fallback.
/// <para>SP-0172: once the endpoint has reported titles, one failed poll no longer ends the loop - a
/// bounded streak of consecutive failures is waited out (a transient 503 or restart would otherwise
/// send the caller to a second full-bitrate connection for the rest of the session), and giving up
/// still records that titles were reported.</para>
/// </remarks>
public sealed class IcecastStatusReader
{
    public const int MaximumDocumentBytes = 128 * 1024;

    /// <summary>
    /// SP-0172: the number of consecutive failed polls that ends the loop once titles have been
    /// reported. Three misses at <see cref="PollInterval"/> ride out a brief server restart or a
    /// momentary 503 without paying for a second audio connection, while a genuinely dead endpoint is
    /// still given up on inside a few minutes.
    /// </summary>
    public const int MaximumConsecutiveFailures = 3;

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _pollInterval;

    public IcecastStatusReader(HttpClient httpClient)
        : this(httpClient, RequestTimeout)
    {
    }

    /// <summary>Tests shorten the deadline; the product always uses <see cref="RequestTimeout"/>.</summary>
    internal IcecastStatusReader(HttpClient httpClient, TimeSpan requestTimeout)
        : this(httpClient, requestTimeout, PollInterval)
    {
    }

    /// <summary>Tests shorten the cadence; the product always uses <see cref="PollInterval"/>.</summary>
    internal IcecastStatusReader(HttpClient httpClient, TimeSpan requestTimeout, TimeSpan pollInterval)
    {
        _httpClient = httpClient;
        _requestTimeout = requestTimeout;
        _pollInterval = pollInterval;
    }

    public async Task<IcecastStatusReadResult> ReadAsync(
        Uri streamUri,
        IProgress<string?> onTitleChanged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        ArgumentNullException.ThrowIfNull(onTitleChanged);

        var endpoint = new Uri(streamUri.GetLeftPart(UriPartial.Authority) + "/status-json.xsl");
        string? lastReported = null;
        var reportedAny = false;
        var failedPolls = 0;

        try
        {
            while (true)
            {
                var (failure, title) = await PollAsync(endpoint, streamUri, cancellationToken);
                if (failure is null)
                {
                    failedPolls = 0;
                    if (!string.Equals(title, lastReported, StringComparison.Ordinal))
                    {
                        lastReported = title;
                        reportedAny |= title is not null;
                        onTitleChanged.Report(title);
                    }
                }
                else if (!reportedAny || ++failedPolls >= MaximumConsecutiveFailures)
                {
                    // Before the first title, tolerance would only delay the caller's fallback to its
                    // other reader for an endpoint that has never worked. After titles, the streak is
                    // what a transient failure is waited out with - and giving up says the endpoint
                    // was working, by carrying TitlesReported.
                    return new(failure.Value, reportedAny);
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(IcecastStatusReadOutcome.Cancelled, reportedAny);
        }
    }

    /// <summary>One status request. Returns its title, or the reason the poll failed.</summary>
    private async Task<(IcecastStatusReadOutcome? Failure, string? Title)> PollAsync(
        Uri endpoint,
        Uri streamUri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestDeadline.CancelAfter(_requestTimeout);
            using var response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (IcecastStatusReadOutcome.EndpointUnavailable, null);
            }

            // SP-0129: the body is read under the same request deadline as the head. It used to be read
            // under the caller's token only, on a client with no timeout, so a server that answered and
            // then held the body open blocked the now-playing fallback for the whole session.
            var payload = await ReadDocumentAsync(response, _requestTimeout, requestDeadline.Token);
            if (payload is null)
            {
                return (IcecastStatusReadOutcome.Malformed, null);
            }

            var parse = IcecastStatusParser.ExtractTitle(payload, streamUri, out var title);
            return parse switch
            {
                IcecastStatusParse.MatchedMount => (null, title),
                IcecastStatusParse.NoMatchingMount => (IcecastStatusReadOutcome.NoMatchingMount, null),
                _ => (IcecastStatusReadOutcome.Malformed, null),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller tore the read down; that ending belongs to ReadAsync, not this poll
        }
        catch (Exception failure) when (failure is OperationCanceledException or HttpRequestException or IOException or TimeoutException)
        {
            // The poll's own deadline (OperationCanceledException), a refusal mid-body, or a body that
            // never finished: the endpoint did not answer as an endpoint.
            return (IcecastStatusReadOutcome.EndpointUnavailable, null);
        }
    }

    /// <returns>The document, or <c>null</c> when it is larger than <see cref="MaximumDocumentBytes"/>.</returns>
    private static async Task<string?> ReadDocumentAsync(
        HttpResponseMessage response, TimeSpan requestTimeout, CancellationToken requestDeadline)
    {
        try
        {
            // The deadline token already bounds the whole read; the silence bound equal to it only lets the
            // shared loop do the ceiling and the short-read check.
            var bytes = await HttpDownload.ReadAllBytesAsync(
                response, progress: null, MaximumDocumentBytes, requestTimeout, requestDeadline);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
