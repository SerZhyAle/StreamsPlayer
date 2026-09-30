using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads ICY/Shoutcast now-playing metadata from an audio stream over a dedicated,
/// best-effort HTTP(S) connection, independent of whichever engine plays the audio (Core has no media
/// dependency, and the reader predates the LibVLC engine).
/// Reports each changed <c>StreamTitle</c> and never throws: a missing, malformed,
/// or unreachable metadata source must not disturb playback.
/// </summary>
/// <remarks>
/// SP-0074 changed two things about that promise. It still never throws, but it now <em>reports</em> how
/// the attempt ended (<see cref="IcyReadOutcome"/>) instead of swallowing it, because a silent failure
/// and a station that carries no metadata were the same observable event - which made the feature look
/// broken and undiagnosable at once. And it now reaches a class of station it never could: a Shoutcast v1
/// daemon greets with <c>ICY 200 OK</c>, and .NET's HTTP stack refuses that reply before reading a single
/// header, so those stations produced nothing however well they were behaving.
/// </remarks>
public sealed class IcyMetadataReader
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private const int MetadataBlockUnit = 16;

    /// <summary>
    /// SP-0129: how long the live body may deliver nothing before the read is abandoned as
    /// <see cref="IcyReadOutcome.TimedOut"/>. A playing station sends audio continuously, so this much
    /// silence is a dead connection; without it a stalled socket held this read open until playback ended.
    /// </summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest <c>icy-metaint</c> this reader will honour. The value sizes a buffer allocated per
    /// attempt from a number an untrusted server chose, so it is bounded rather than trusted.
    /// </summary>
    private const int MaxMetaInterval = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _silenceTimeout;

    public IcyMetadataReader(HttpClient httpClient)
        : this(httpClient, ConnectTimeout, SilenceTimeout)
    {
    }

    /// <summary>Tests shorten the deadlines; the product always uses the constants above.</summary>
    internal IcyMetadataReader(HttpClient httpClient, TimeSpan connectTimeout, TimeSpan silenceTimeout)
    {
        _httpClient = httpClient;
        _connectTimeout = connectTimeout;
        _silenceTimeout = silenceTimeout;
    }

    /// <summary>
    /// SP-0131: the decoding this read's titles needed - the single-byte one when any block was not valid
    /// UTF-8, <see cref="IcyTextEncoding.Utf8"/> when every block was, <c>null</c> when no block arrived.
    /// The App logs it beside the outcome, so a station whose titles still look wrong can be traced to the
    /// choice that produced them.
    /// </summary>
    public IcyTextEncoding? TextEncoding { get; private set; }

    /// <summary>
    /// Streams metadata updates until <paramref name="cancellationToken"/> is cancelled
    /// or the stream ends. Reports <c>null</c> only when a block clears the title;
    /// otherwise reports the sanitized track text. Returns without reporting when the
    /// stream carries no ICY metadata.
    /// </summary>
    /// <returns>
    /// How the attempt ended, and whether titles were reported before then (SP-0172), for the caller to
    /// log. Never throws: the mapping below is what turns a failure into a value, and playback is never
    /// disturbed by anything on this path.
    /// </returns>
    public async Task<IcyReadResult> ReadAsync(string url, IProgress<string?> onTitleChanged, CancellationToken cancellationToken)
    {
        // A live station is almost always torn down mid-read rather than ending on its own, so without
        // this the common ending would be a bare "Cancelled" and the log could not tell a station that
        // was feeding us tracks from one that connected and never said a word - which is the distinction
        // the whole ticket exists to make.
        var sink = new TitleSink(onTitleChanged);
        try
        {
            return await ReadCoreAsync(url, sink, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Playback stopped, switched, or failed.
            return new(IcyReadOutcome.Cancelled, sink.ReportedAny);
        }
        catch (OperationCanceledException)
        {
            return new(IcyReadOutcome.TimedOut, sink.ReportedAny); // our own connect deadline, not the caller's teardown
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException)
        {
            return new(IcyReadOutcome.Unreachable, sink.ReportedAny);
        }
        catch
        {
            // Best-effort: any remaining protocol or decoding failure leaves the caller's station-only
            // presentation intact. Core stays log-free; the value is what the App reports.
            return new(IcyReadOutcome.Malformed, sink.ReportedAny);
        }
    }

    private async Task<IcyReadResult> ReadCoreAsync(string url, TitleSink onTitleChanged, CancellationToken cancellationToken)
    {
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectDeadline.CancelAfter(_connectTimeout);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                connectDeadline.Token);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.InvalidResponse)
        {
            // SP-0074: a Shoutcast v1 daemon answered "ICY 200 OK" and the standard stack refused the
            // reply before reading a header. Typed error, not the message text: matching "invalid status
            // line" would break on a localized runtime.
            return await ReadViaSocketAsync(url, onTitleChanged, connectDeadline.Token, cancellationToken);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            if (!TryGetMetaInterval(response, out var metaInterval))
            {
                return new(IcyReadOutcome.NoMetadataOffered, onTitleChanged.ReportedAny); // the station says it carries no metadata
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await PumpAsync(stream, metaInterval, onTitleChanged, cancellationToken);
        }
    }

    /// <summary>
    /// The fallback for a station whose greeting the standard stack will not accept.
    /// </summary>
    /// <remarks>
    /// Plaintext only, deliberately: a Shoutcast v1 server is an HTTP/1.0-era daemon and does not serve
    /// TLS, so hand-rolling a TLS handshake would add the largest part of the risk for a case that does
    /// not arise. An <c>https</c> URL that reaches here is reported and left alone.
    /// <para>The request is HTTP/1.0 with <c>Connection: close</c> so that chunked encoding and
    /// keep-alive cannot exist on this socket - the head parser deliberately understands neither.</para>
    /// <para>Costs one further connection, once. Nothing here retries: the first connection was refused
    /// by our own stack rather than by the station, and a station that drops this one is left alone until
    /// the channel is launched again.</para>
    /// </remarks>
    internal async Task<IcyReadResult> ReadViaSocketAsync(
        string url,
        IProgress<string?> onTitleChanged,
        CancellationToken connectDeadline,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            return new(IcyReadOutcome.StatusLineRefused, TitlesReported: false);
        }

        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.IsDefaultPort ? 80 : uri.Port, connectDeadline);
        await using var stream = client.GetStream();

        var request =
            $"GET {uri.PathAndQuery} HTTP/1.0\r\n" +
            $"Host: {uri.Host}\r\n" +
            "User-Agent: StreamsPlayer/0.1\r\n" +
            "Icy-MetaData: 1\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), connectDeadline);

        var head = await IcyResponseHead.ReadAsync(stream, connectDeadline);
        if (head is null || head.StatusCode != 200)
        {
            // Reached before any body byte, so no title can have been reported yet.
            return new(IcyReadOutcome.Malformed, TitlesReported: false);
        }

        if (!TryParseMetaInterval(head["icy-metaint"], out var metaInterval))
        {
            return new(IcyReadOutcome.NoMetadataOffered, TitlesReported: false);
        }

        return await PumpAsync(stream, metaInterval, onTitleChanged, cancellationToken);
    }

    private static bool TryGetMetaInterval(HttpResponseMessage response, out int metaInterval)
    {
        if (response.Headers.TryGetValues("icy-metaint", out var values) ||
            response.Content.Headers.TryGetValues("icy-metaint", out values))
        {
            foreach (var value in values)
            {
                if (TryParseMetaInterval(value, out metaInterval))
                {
                    return true;
                }
            }
        }

        metaInterval = 0;
        return false;
    }

    /// <summary>
    /// One place for the rule, so the socket path and the HTTP path cannot bound it differently.
    /// </summary>
    private static bool TryParseMetaInterval(string? value, out int metaInterval) =>
        (metaInterval = int.TryParse(value, out var parsed) && parsed > 0 && parsed <= MaxMetaInterval ? parsed : 0) > 0;

    private async Task<IcyReadResult> PumpAsync(
        Stream stream,
        int metaInterval,
        IProgress<string?> onTitleChanged,
        CancellationToken cancellationToken)
    {
        var audioBuffer = new byte[metaInterval];
        var lengthBuffer = new byte[1];
        string? lastReported = null;
        var reportedAny = false;
        // Linked, so its expiry surfaces as a cancellation the caller's token did not ask for - the
        // TimedOut branch of ReadAsync - and never as the user's own stop.
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Discard the audio segment; we only want the metadata that follows it.
            if (!await ReadExactlyAsync(stream, audioBuffer, metaInterval, silence))
            {
                return Ended(reportedAny);
            }

            if (!await ReadExactlyAsync(stream, lengthBuffer, 1, silence))
            {
                return Ended(reportedAny);
            }

            var metaLength = lengthBuffer[0] * MetadataBlockUnit;
            if (metaLength == 0)
            {
                continue; // No metadata change in this interval.
            }

            var metaBuffer = new byte[metaLength];
            if (!await ReadExactlyAsync(stream, metaBuffer, metaLength, silence))
            {
                return Ended(reportedAny);
            }

            var block = IcyTextDecoder.Decode(metaBuffer, out var encoding);
            // A single-byte choice is the notable one; a later UTF-8 block never overwrites it.
            if (TextEncoding is null or IcyTextEncoding.Utf8)
            {
                TextEncoding = encoding;
            }

            var title = IcyMetadataParser.ExtractStreamTitle(block);
            if (!string.Equals(title, lastReported, StringComparison.Ordinal))
            {
                lastReported = title;
                reportedAny |= title is not null;
                onTitleChanged.Report(title);
            }
        }

        return Ended(reportedAny);
    }

    /// <summary>
    /// A stream that ended is recorded as ended; whether it said anything first rides on the result's
    /// flag - the distinction is the whole point of the log line this feeds.
    /// </summary>
    private static IcyReadResult Ended(bool reportedAny) =>
        new(IcyReadOutcome.StreamEnded, reportedAny);

    /// <summary>
    /// Forwards every title to the caller and remembers whether any real one went through, so the
    /// outcome can distinguish "this station was announcing tracks" from "this station said nothing"
    /// even when the read ends by cancellation - which is how a live station's read almost always ends.
    /// </summary>
    private sealed class TitleSink(IProgress<string?> inner) : IProgress<string?>
    {
        public bool ReportedAny { get; private set; }

        public void Report(string? value)
        {
            // A null clears the line and is not an announcement; only real text counts as "we read it".
            ReportedAny |= value is not null;
            inner.Report(value);
        }
    }

    private async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        int count,
        CancellationTokenSource silence)
    {
        var offset = 0;
        while (offset < count)
        {
            // Re-armed before every read, so the bound measures silence and not the length of the session.
            silence.CancelAfter(_silenceTimeout);
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), silence.Token);
            if (read == 0)
            {
                return false; // Stream ended mid-frame.
            }

            offset += read;
        }

        return true;
    }
}
