using System.Net.Http;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // Dedicated client for the best-effort ICY metadata connection. Its timeout is
    // infinite because the read is long-lived and bounded only by _icyCts; the
    // streaming read must not be cut by the shared 30 s catalog-client timeout.
    private readonly HttpClient _icyHttpClient = CreateIcyHttpClient();
    private CancellationTokenSource? _icyCts;

    // Bumped on every start/stop so a marshaled report from a superseded reader is
    // dropped instead of overwriting the current station's now-playing line.
    private int _nowPlayingGeneration;

    private static HttpClient CreateIcyHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamsPlayer/0.1");
        return client;
    }

    private void StartNowPlayingMetadata(StreamChannel channel)
    {
        // Metadata is requested only as part of an explicit HTTP(S) audio attempt.
        if (!Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        var generation = ++_nowPlayingGeneration;
        var cts = new CancellationTokenSource();
        _icyCts = cts;

        // Constructed on the UI thread, so the callback marshals back to it.
        var progress = new Progress<string?>(title => OnNowPlayingTitle(generation, title));
        _ = new IcyMetadataReader(_icyHttpClient).ReadAsync(channel.Url, progress, cts.Token);
    }

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
            _ = PersistNowPlayingHistoryAsync(_playingAudio.Channel.Id, title);
        }

        // SP-0021: mirror the current track into the Windows media session title (no-op when off).
        UpdateSystemMediaMetadata(string.IsNullOrWhiteSpace(title) ? null : title);
    }

    // Best-effort history track-text update: no reorder, no new row, and no disk write when the entry
    // is absent or the text is unchanged (UpdateTrackText returns null). Fire-and-forget on the UI thread.
    private async Task PersistNowPlayingHistoryAsync(Guid channelId, string? title)
    {
        var updated = ListeningHistory.UpdateTrackText(_state.ListeningHistory, channelId, title);
        if (updated is null)
        {
            return;
        }

        _state = await _store.SaveAsync(_state with { ListeningHistory = updated });
    }
}
