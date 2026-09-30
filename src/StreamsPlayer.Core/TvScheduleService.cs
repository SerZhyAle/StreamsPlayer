using System.Net.Http;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0075: one explicit download of a user-supplied XMLTV address. There is no other caller and no
/// schedule of calls: the product never fetches a schedule on its own (strategic constraint, and the
/// same rule the catalog follows).
/// </summary>
public sealed class TvScheduleService(HttpClient httpClient)
{
    /// <summary>
    /// A usable schedule address: absolute http or https. Anything else is refused before any request,
    /// so a typo cannot turn into a file read or an unexpected protocol.
    /// </summary>
    public static bool TryParseSource(string? text, out Uri source)
    {
        source = null!;
        if (string.IsNullOrWhiteSpace(text) ||
            !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        source = uri;
        return true;
    }

    /// <exception cref="HttpRequestException">The server refused or could not be reached.</exception>
    /// <exception cref="InvalidDataException">Too large, not XML, or not XMLTV.</exception>
    /// <exception cref="TimeoutException">No response head in time, or the transfer went silent.</exception>
    /// <exception cref="OperationCanceledException">Cancelled by the user.</exception>
    public async Task<TvScheduleDocument> DownloadAsync(
        Uri source,
        DateTimeOffset now,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await HttpDownload
            .GetForHeadersAsync(httpClient, source, TvScheduleLimits.HeaderTimeout, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await HttpDownload.ReadAllBytesAsync(
                response, progress, TvScheduleLimits.MaximumDownloadBytes, TvScheduleLimits.IdleTimeout, cancellationToken)
            .ConfigureAwait(false);
        var channels = await Task.Run(() => XmltvParser.Parse(body, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new TvScheduleDocument(TvScheduleDocument.CurrentSchemaVersion, source.ToString(), now, channels);
    }
}
