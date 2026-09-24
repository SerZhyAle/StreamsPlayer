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

    public IcecastStatusReader(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
                requestDeadline.CancelAfter(RequestTimeout);
                using var response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return IcecastStatusReadOutcome.EndpointUnavailable;
                }

                var payload = await ReadDocumentAsync(response, cancellationToken);
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
    }

    private static async Task<string?> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        await using var body = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > MaximumDocumentBytes)
            {
                return null;
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length));
    }
}
