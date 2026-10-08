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

    /// <summary>How many redirects on the consented host are followed before the address is given up on.</summary>
    public const int MaximumRedirects = 5;

    /// <summary>
    /// Whether a redirect may be followed: the user agreed to download from one host, so the target keeps that
    /// host and never steps from https down to http.
    /// </summary>
    public static bool IsRedirectAllowed(Uri source, Uri target) =>
        string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
        (target.Scheme == Uri.UriSchemeHttps ||
         (target.Scheme == Uri.UriSchemeHttp && source.Scheme == Uri.UriSchemeHttp));

    /// <remarks>
    /// <paramref name="httpClient"/> must not follow redirects itself (<c>AllowAutoRedirect = false</c>): they are
    /// followed here, one hop at a time, so that none reaches a host the user did not agree to.
    /// </remarks>
    /// <exception cref="HttpRequestException">The server refused or could not be reached, or redirected away from the consented host.</exception>
    /// <exception cref="InvalidDataException">Too large, not XML, or not XMLTV.</exception>
    /// <exception cref="TimeoutException">No response head in time, or the transfer went silent.</exception>
    /// <exception cref="OperationCanceledException">Cancelled by the user.</exception>
    public async Task<TvScheduleDocument> DownloadAsync(
        Uri source,
        DateTimeOffset now,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await GetFollowingConsentedRedirectsAsync(source, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await HttpDownload.ReadAllBytesAsync(
                response, progress, TvScheduleLimits.MaximumDownloadBytes, TvScheduleLimits.IdleTimeout, cancellationToken)
            .ConfigureAwait(false);
        var channels = await Task.Run(() => XmltvParser.Parse(body, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new TvScheduleDocument(TvScheduleDocument.CurrentSchemaVersion, source.ToString(), now, channels);
    }

    private async Task<HttpResponseMessage> GetFollowingConsentedRedirectsAsync(Uri source, CancellationToken cancellationToken)
    {
        var current = source;
        for (var hop = 0; ; hop++)
        {
            var response = await HttpDownload
                .GetForHeadersAsync(httpClient, current, TvScheduleLimits.HeaderTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirectStatus(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            using (response)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsRedirectAllowed(source, target))
                {
                    throw new HttpRequestException(
                        $"The schedule address redirected to {target.Host}, which is not the host that was agreed to ({source.Host}).");
                }

                if (hop >= MaximumRedirects)
                {
                    throw new HttpRequestException("The schedule address redirected too many times.");
                }

                current = target;
            }
        }
    }

    private static bool IsRedirectStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;
}
