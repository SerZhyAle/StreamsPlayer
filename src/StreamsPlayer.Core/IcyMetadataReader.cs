using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads ICY/Shoutcast now-playing metadata from an audio stream over a dedicated,
/// best-effort HTTP(S) connection (WPF <c>MediaElement</c> exposes no ICY API).
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
    private const int ConnectTimeoutSeconds = 15;
    private const int MetadataBlockUnit = 16;

    /// <summary>
    /// The largest <c>icy-metaint</c> this reader will honour. The value sizes a buffer allocated per
    /// attempt from a number an untrusted server chose, so it is bounded rather than trusted.
    /// </summary>
    private const int MaxMetaInterval = 1024 * 1024;

    private readonly HttpClient _httpClient;

    public IcyMetadataReader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Streams metadata updates until <paramref name="cancellationToken"/> is cancelled
    /// or the stream ends. Reports <c>null</c> only when a block clears the title;
    /// otherwise reports the sanitized track text. Returns without reporting when the
    /// stream carries no ICY metadata.
    /// </summary>
    /// <returns>
    /// How the attempt ended, for the caller to log. Never throws: the mapping below is what turns a
    /// failure into a value, and playback is never disturbed by anything on this path.
    /// </returns>
    public async Task<IcyReadOutcome> ReadAsync(string url, IProgress<string?> onTitleChanged, CancellationToken cancellationToken)
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
            return sink.ReportedAny ? IcyReadOutcome.TitlesReported : IcyReadOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            return IcyReadOutcome.TimedOut; // our own connect deadline, not the caller's teardown
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException)
        {
            return IcyReadOutcome.Unreachable;
        }
        catch
        {
            // Best-effort: any remaining protocol or decoding failure leaves the caller's station-only
            // presentation intact. Core stays log-free; the value is what the App reports.
            return IcyReadOutcome.Malformed;
        }
    }

    private async Task<IcyReadOutcome> ReadCoreAsync(string url, IProgress<string?> onTitleChanged, CancellationToken cancellationToken)
    {
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectDeadline.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));

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
                return IcyReadOutcome.NoMetadataOffered; // the station says it carries no metadata
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
    private static async Task<IcyReadOutcome> ReadViaSocketAsync(
        string url,
        IProgress<string?> onTitleChanged,
        CancellationToken connectDeadline,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            return IcyReadOutcome.StatusLineRefused;
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
            return IcyReadOutcome.Malformed;
        }

        if (!TryParseMetaInterval(head["icy-metaint"], out var metaInterval))
        {
            return IcyReadOutcome.NoMetadataOffered;
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

    private static async Task<IcyReadOutcome> PumpAsync(
        Stream stream,
        int metaInterval,
        IProgress<string?> onTitleChanged,
        CancellationToken cancellationToken)
    {
        var audioBuffer = new byte[metaInterval];
        var lengthBuffer = new byte[1];
        string? lastReported = null;
        var reportedAny = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Discard the audio segment; we only want the metadata that follows it.
            if (!await ReadExactlyAsync(stream, audioBuffer, metaInterval, cancellationToken))
            {
                return Ended(reportedAny);
            }

            if (!await ReadExactlyAsync(stream, lengthBuffer, 1, cancellationToken))
            {
                return Ended(reportedAny);
            }

            var metaLength = lengthBuffer[0] * MetadataBlockUnit;
            if (metaLength == 0)
            {
                continue; // No metadata change in this interval.
            }

            var metaBuffer = new byte[metaLength];
            if (!await ReadExactlyAsync(stream, metaBuffer, metaLength, cancellationToken))
            {
                return Ended(reportedAny);
            }

            var block = Encoding.UTF8.GetString(metaBuffer);
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
    /// A stream that ended after saying something is a success that finished, not a failure - the
    /// distinction is the whole point of the log line this feeds.
    /// </summary>
    private static IcyReadOutcome Ended(bool reportedAny) =>
        reportedAny ? IcyReadOutcome.TitlesReported : IcyReadOutcome.StreamEnded;

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

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                return false; // Stream ended mid-frame.
            }

            offset += read;
        }

        return true;
    }
}
