using System.IO;
using System.Net.Http;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// What a source said about its renditions. <paramref name="Reason"/> is why there is no ladder, and it
/// exists because "this channel offers one quality" and "we could not tell" must not read the same in
/// an archived log (SP-0071 criterion 7).
/// </summary>
internal readonly record struct StreamQualityLadderReading(IReadOnlyList<StreamQualityRung> Rungs, string Reason);

/// <summary>
/// SP-0071: fetches the HLS master playlist so the player knows what it may choose between. Modelled on
/// <see cref="PlaybackStatusProbe"/> - a shared client, http/https only, a hard deadline, and every exit
/// path a value rather than an exception at the caller.
/// </summary>
/// <remarks>
/// Runs once per player window and only after the stream has reached live, so it can never make the
/// first open slower - a constraint of the ticket. The reading is one small text body; nothing here
/// fetches a variant playlist or a segment, because a probe that downloads media would compete for the
/// very bandwidth it is measuring.
/// </remarks>
internal static class StreamQualityLadderProbe
{
    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// A master playlist is a few hundred bytes. This ceiling is not a memory guard so much as a
    /// statement that a body this large is not a playlist we can use, whatever it is.
    /// </summary>
    private const long BodyCeilingBytes = 256 * 1024;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(3);

    public static async Task<StreamQualityLadderReading> ReadAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            // DASH, RTSP and progressive sources are deliberately out of scope: the ceiling is only ever
            // applied where a ladder was actually read, so they keep exactly today's behaviour.
            return new StreamQualityLadderReading([], "not_hls");
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(Deadline);
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new StreamQualityLadderReading([], "fetch_failed");
            }

            var body = await HttpDownload.ReadAllBytesAsync(response, null, BodyCeilingBytes, IdleTimeout, deadline.Token);
            var text = Encoding.UTF8.GetString(body);
            var rungs = StreamQualityLadder.Read(text);
            return rungs.Count >= 2
                ? new StreamQualityLadderReading(rungs, "ok")
                : new StreamQualityLadderReading([], WhyThereIsNoLadder(text));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
                                      InvalidDataException or TimeoutException or InvalidOperationException)
        {
            return new StreamQualityLadderReading([], "fetch_failed");
        }
    }

    /// <summary>
    /// Separates the three ways a fetched playlist can yield nothing. The reader itself reports one empty
    /// list for all of them by design - it answers "may a ceiling be trusted", not "why not" - so the
    /// distinction the log needs is drawn here, by counting the variant tags in the body.
    /// </summary>
    /// <remarks>
    /// Counting the plain tag as a substring is exact rather than approximate: the trick-play tag
    /// <c>#EXT-X-I-FRAME-STREAM-INF:</c> does not contain <c>#EXT-X-STREAM-INF:</c>.
    /// </remarks>
    private static string WhyThereIsNoLadder(string text) =>
        (text.Split("#EXT-X-STREAM-INF:").Length - 1) switch
        {
            0 => "not_master",        // a media playlist, or not a playlist at all
            1 => "single_rendition",  // one quality: nothing to choose between
            _ => "incomplete"         // renditions that do not declare what a ceiling needs
        };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        return client;
    }
}
