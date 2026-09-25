using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Polls Icecast's <c>status-json.xsl</c> endpoint for the mount currently being played.
/// </summary>
/// <remarks>
/// Unlike an ICY reader this never consumes media bytes: each bounded status document is a small request
/// to the stream's own origin. A compatible endpoint is polled only while its channel is playing; an
/// absent or malformed endpoint returns promptly so callers can retain their existing metadata fallback.
/// </remarks>
public sealed class IcecastStatusReader
{
    public const int MaximumDocumentBytes = 128 * 1024;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public IcecastStatusReader(HttpClient httpClient)
        : this(httpClient, RequestTimeout)
    {
    }

    /// <summary>Tests shorten the deadline; the product always uses <see cref="RequestTimeout"/>.</summary>
    internal IcecastStatusReader(HttpClient httpClient, TimeSpan requestTimeout)
    {
        _httpClient = httpClient;
        _requestTimeout = requestTimeout;
    }

    public async Task<IcecastStatusReadOutcome> ReadAsync(
        Uri streamUri,
        IProgress<string?> onTitleChanged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        ArgumentNullException.ThrowIfNull(onTitleChanged);

        var endpoint = new Uri(streamUri.GetLeftPart(UriPartial.Authority) + "/status-json.xsl");
        string? lastReported = null;
        var reportedAny = false;

        try
        {
            while (true)
            {
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestDeadline.CancelAfter(_requestTimeout);
                using var response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return IcecastStatusReadOutcome.EndpointUnavailable;
                }

                // SP-0129: the body is read under the same request deadline as the head. It used to be read
                // under the caller's token only, on a client with no timeout, so a server that answered and
                // then held the body open blocked the now-playing fallback for the whole session.
                var payload = await ReadDocumentAsync(response, _requestTimeout, requestDeadline.Token);
                if (payload is null || !IcecastStatusParser.TryExtractTitle(payload, streamUri, out var title))
                {
                    return IcecastStatusReadOutcome.Malformed;
                }

                if (!string.Equals(title, lastReported, StringComparison.Ordinal))
                {
                    lastReported = title;
                    reportedAny |= title is not null;
                    onTitleChanged.Report(title);
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return reportedAny ? IcecastStatusReadOutcome.TitlesReported : IcecastStatusReadOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            return IcecastStatusReadOutcome.EndpointUnavailable;
        }
        catch (HttpRequestException)
        {
            return IcecastStatusReadOutcome.EndpointUnavailable;
        }
        catch (IOException)
        {
            return IcecastStatusReadOutcome.EndpointUnavailable;
        }
        catch (TimeoutException)
        {
            return IcecastStatusReadOutcome.EndpointUnavailable;
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
