using System.Net.Http;
using System.Net.Http.Headers;

namespace StreamsPlayer.App;

/// <summary>
/// Failure-path-only HTTP status probe. Media backends (LibVLC, WPF <c>MediaElement</c>) hide the HTTP
/// status of a failed open, so recovery cannot otherwise tell a retryable 429/5xx from a permanent non-429
/// 4xx (SP-0015 / <c>streams.txt</c> Part D). This reads that status on demand, http/https only, best-effort:
/// a null result (non-http(s) URL such as RTSP, or any probe error) is treated as transient by the classifier.
/// It runs only when a foreground playback attempt has already failed — never on the grid-preview path.
/// </summary>
internal static class PlaybackStatusProbe
{
    private static readonly HttpClient Client = CreateClient();

    public static async Task<int?> TryGetStatusAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            // Range 0-0 asks for a single byte; with ResponseHeadersRead the body is never pulled.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Headers = { Range = new RangeHeaderValue(0, 0) } };
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        return client;
    }
}
