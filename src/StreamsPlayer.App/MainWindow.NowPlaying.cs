using System.Net.Http;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // Dedicated client for the best-effort ICY metadata connection. Its timeout is
    // infinite because the read is long-lived and bounded only by _icyCts; the
    // streaming read must not be cut by the shared 30 s catalog-client timeout.
    private readonly HttpClient _icyHttpClient = CreateIcyHttpClient();
    private readonly HttpClient _statusHttpClient = CreateStatusHttpClient();
    private CancellationTokenSource? _icyCts;
    private readonly Dictionary<Guid, string?> _pendingNowPlayingHistory = [];
    private readonly DispatcherTimer _nowPlayingHistorySaveTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    // Bumped on every start/stop so a marshaled report from a superseded reader is
    // dropped instead of overwriting the current station's now-playing line.
    private int _nowPlayingGeneration;

    private static HttpClient CreateIcyHttpClient()
    {
        return CreateNowPlayingHttpClient();
    }

    private static HttpClient CreateStatusHttpClient()
    {
        // A redirect can leave the broadcaster's own origin, which this feature must never contact.
        return CreateNowPlayingHttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    private static HttpClient CreateNowPlayingHttpClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        return client;
    }

    private void StartNowPlayingMetadata(StreamChannel channel)
    {
        // A recovery reconnect restarts playback without going through StopAudioPlayback, so the previous
        // reader must be cancelled here. Its pump loop only ends on cancellation or end-of-stream, and a
        // live station never ends: an orphan would keep draining the stream at full bitrate for the rest
        // of the session, invisibly - the generation guard suppresses its reports but not its reads.
        StopNowPlayingMetadata();

        // Metadata is requested only as part of an explicit HTTP(S) audio attempt.
        if (!LaunchableAddress.TryParseHttp(channel.Url, out var uri))
        {
            return;
        }

        var generation = ++_nowPlayingGeneration;
        var cts = new CancellationTokenSource();
        _icyCts = cts;

        // Constructed on the UI thread, so the callback marshals back to it.
        var progress = new Progress<string?>(title => OnNowPlayingTitle(generation, title));
        _ = ReadNowPlayingMetadataAsync(uri, channel.Url, progress, cts.Token);
    }

    /// <summary>
    /// SP-0074: one line per attempt, so an archived session can answer why a station showed no track.
    /// Before this, every failure was swallowed and a station that could not be read looked exactly like
    /// a station that announces nothing. SP-0172: each outcome also carries whether titles were
    /// reported before it ended, so a reader that was working and then stopped is not logged as one
    /// that never worked.
    /// </summary>
    /// <remarks>
    /// The host, never the address: a catalog URL may carry credentials and <see cref="Uri.Host"/> cannot.
    /// <c>Cancelled</c> is logged too - it is the ordinary teardown and therefore the common value, but
    /// without it a log in which a station simply stops appearing could not be told from one where the
    /// read never started.
    /// </remarks>
    private async Task ReadNowPlayingMetadataAsync(
        Uri uri,
        string url,
        IProgress<string?> progress,
        CancellationToken cancellationToken)
    {
        var status = await new IcecastStatusReader(_statusHttpClient).ReadAsync(uri, progress, cancellationToken);
        _log.Event("STATUS METADATA", $"outcome={status.Outcome}", $"titles={BoolText(status.TitlesReported)}", $"host={uri.Host}");

        if (cancellationToken.IsCancellationRequested || status.Outcome is IcecastStatusReadOutcome.Cancelled)
        {
            // Playback stopped or switched: nothing to fall back from.
            return;
        }

        if (status.TitlesReported)
        {
            // SP-0172: the endpoint worked and then gave up, so this fallback costs a second
            // full-bitrate connection until the channel is relaunched - the expense SP-0131 built this
            // feature to avoid. It is still the only remaining source of titles, but the log has to say
            // why the session is paying for it.
            _log.Event(
                "STATUS METADATA",
                "fallback=icy",
                $"reason={status.Outcome}",
                "the status endpoint reported titles and then kept failing; opening the full-stream ICY read");
        }

        // A compatible status endpoint replaces the old full-stream metadata read for this session. Only
        // an absent or malformed endpoint - or one that gave up after reporting titles, above - reaches
        // ICY, so a normal Icecast server costs small status documents instead of a second continuous
        // audio transfer.
        var reader = new IcyMetadataReader(_icyHttpClient);
        var outcome = await reader.ReadAsync(url, progress, cancellationToken);
        // SP-0131: the decoding the titles needed, so a station whose text still looks wrong can be traced.
        _log.Event(
            "ICY METADATA",
            $"outcome={outcome.Outcome}",
            $"titles={BoolText(outcome.TitlesReported)}",
            $"text={reader.TextEncoding?.ToString() ?? "none"}",
            $"host={uri.Host}");
    }

    private static string BoolText(bool value) => value ? "true" : "false";

    private void StopNowPlayingMetadata()
    {
        _nowPlayingGeneration++;
        _icyCts?.Cancel();
        _icyCts?.Dispose();
        _icyCts = null;
    }

    private void OnNowPlayingTitle(int generation, string? title)
    {
        // Drop reports from a reader that a stop/switch has already superseded, and
        // any report that arrives after playback has ended.
        if (generation != _nowPlayingGeneration || _playingAudio is null)
        {
            return;
        }

        var station = _playingAudio.DisplayTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            SetNowPlaying("NowPlaying", station);
        }
        else
        {
            SetNowPlaying("NowPlayingWithTrack", station, title);
            // SP-0019: fold the latest observed track text into this channel's history entry. A blank
            // title never overwrites a good line; the entry already exists (created at MediaOpened).
            QueueNowPlayingHistory(_playingAudio.Channel.Id, title);
        }

        // SP-0021: mirror the current track into the Windows media session title (no-op when off).
        UpdateSystemMediaMetadata(string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private void QueueNowPlayingHistory(Guid channelId, string? title)
    {
        _pendingNowPlayingHistory[channelId] = title;
        if (!_nowPlayingHistorySaveTimer.IsEnabled)
        {
            _nowPlayingHistorySaveTimer.Tick -= NowPlayingHistorySaveTimer_Tick;
            _nowPlayingHistorySaveTimer.Tick += NowPlayingHistorySaveTimer_Tick;
            _nowPlayingHistorySaveTimer.Start();
        }
    }

    private async void NowPlayingHistorySaveTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _nowPlayingHistorySaveTimer.Stop();
            await FlushPendingNowPlayingHistoryAsync();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(NowPlayingHistorySaveTimer_Tick), exception);
        }
    }

    private async Task FlushPendingNowPlayingHistoryAsync()
    {
        if (_pendingNowPlayingHistory.Count == 0)
        {
            return;
        }

        var pending = _pendingNowPlayingHistory.ToArray();
        _pendingNowPlayingHistory.Clear();
        await PersistAsync(state =>
        {
            var history = state.ListeningHistory;
            foreach (var (channelId, title) in pending)
            {
                history = ListeningHistory.UpdateTrackText(history, channelId, title) ?? history;
            }

            return history == state.ListeningHistory ? state : state with { ListeningHistory = history };
        });
    }
}
