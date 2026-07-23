using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads ICY/Shoutcast now-playing metadata from an audio stream over a dedicated,
/// best-effort HTTP(S) connection (WPF <c>MediaElement</c> exposes no ICY API).
/// Reports each changed <c>StreamTitle</c> and never throws: a missing, malformed,
/// or unreachable metadata source must not disturb playback.
/// </summary>
public sealed class IcyMetadataReader
{
    private const int ConnectTimeoutSeconds = 15;
    private const int MetadataBlockUnit = 16;

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
    public async Task ReadAsync(string url, IProgress<string?> onTitleChanged, CancellationToken cancellationToken)
    {
        try
        {
            await ReadCoreAsync(url, onTitleChanged, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected teardown when playback stops, switches, or fails.
        }
        catch
        {
            // Best-effort: any network, protocol, or decoding failure leaves the
            // caller's station-only presentation intact. Core stays log-free.
        }
    }

    private async Task ReadCoreAsync(string url, IProgress<string?> onTitleChanged, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");

        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectDeadline.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            connectDeadline.Token);
        response.EnsureSuccessStatusCode();

        if (!TryGetMetaInterval(response, out var metaInterval))
        {
            return; // No icy-metaint header: stream has no metadata to report.
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await PumpAsync(stream, metaInterval, onTitleChanged, cancellationToken);
    }

    private static bool TryGetMetaInterval(HttpResponseMessage response, out int metaInterval)
    {
        metaInterval = 0;
        if (response.Headers.TryGetValues("icy-metaint", out var values) ||
            response.Content.Headers.TryGetValues("icy-metaint", out values))
        {
            foreach (var value in values)
            {
                if (int.TryParse(value, out metaInterval) && metaInterval > 0)
                {
                    return true;
                }
            }
        }

        metaInterval = 0;
        return false;
    }

    private static async Task PumpAsync(
        Stream stream,
        int metaInterval,
        IProgress<string?> onTitleChanged,
        CancellationToken cancellationToken)
    {
        var audioBuffer = new byte[metaInterval];
        var lengthBuffer = new byte[1];
        string? lastReported = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Discard the audio segment; we only want the metadata that follows it.
            if (!await ReadExactlyAsync(stream, audioBuffer, metaInterval, cancellationToken))
            {
                return;
            }

            if (!await ReadExactlyAsync(stream, lengthBuffer, 1, cancellationToken))
            {
                return;
            }

            var metaLength = lengthBuffer[0] * MetadataBlockUnit;
            if (metaLength == 0)
            {
                continue; // No metadata change in this interval.
            }

            var metaBuffer = new byte[metaLength];
            if (!await ReadExactlyAsync(stream, metaBuffer, metaLength, cancellationToken))
            {
                return;
            }

            var block = Encoding.UTF8.GetString(metaBuffer);
            var title = IcyMetadataParser.ExtractStreamTitle(block);
            if (!string.Equals(title, lastReported, StringComparison.Ordinal))
            {
                lastReported = title;
                onTitleChanged.Report(title);
            }
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
