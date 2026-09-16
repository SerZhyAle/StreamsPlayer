using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private FastMediaSorterAudioPlayback? _fastMediaSorterAudioPlayback;
    private int _fastMediaSorterAudioGeneration;
    private int _fastMediaSorterAudioLegs;

    private static bool UsesFastMediaSorterAudioRoute(StreamChannel channel) =>
        channel.MediaKind == MediaKind.Audio && FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(channel);

    private void StartFastMediaSorterAudioPlayback(StreamChannel channel, bool reconnecting)
    {
        StopFastMediaSorterAudioPlayback();
        var playback = new FastMediaSorterAudioPlayback();
        var generation = ++_fastMediaSorterAudioGeneration;
        // The generation also moves on every stop, so it is no count of legs; the log wants the count.
        _fastMediaSorterAudioLegs = reconnecting ? _fastMediaSorterAudioLegs + 1 : 1;
        var leg = _fastMediaSorterAudioLegs;
        _fastMediaSorterAudioPlayback = playback;
        playback.Playing += FastMediaSorterAudioPlayback_Playing;
        playback.Ended += FastMediaSorterAudioPlayback_Ended;
        playback.Failed += FastMediaSorterAudioPlayback_Failed;

        _audioOpenTimer.Stop();
        _audioOpenTimer.Start();
        ApplyAudioTransportState();
        // FMS streams have no ICY metadata. More importantly, the reader would consume a second watch
        // listener slot, so this route deliberately never calls StartNowPlayingMetadata.
        StopNowPlayingMetadata();
        _ = OpenFastMediaSorterAudioAsync(playback, generation, leg, channel, reconnecting, _audioRecoveryCts?.Token ?? CancellationToken.None);
    }

    private async Task OpenFastMediaSorterAudioAsync(
        FastMediaSorterAudioPlayback playback,
        int generation,
        int leg,
        StreamChannel channel,
        bool reconnecting,
        CancellationToken cancellationToken)
    {
        var result = await playback.StartAsync(new Uri(channel.Url), _state.AudioVolume, cancellationToken);
        if (!IsCurrentFastMediaSorterPlayback(playback, generation, channel) || result.Cancelled)
        {
            playback.Dispose();
            return;
        }

        _log.Event("FMS AUDIO RESPONSE",
            $"leg={leg}",
            $"reconnecting={reconnecting}",
            $"http={result.StatusCode?.ToString() ?? "n/a"}",
            $"response_ms={result.ResponseElapsed.TotalMilliseconds:F0}",
            $"buffer_ms={FastMediaSorterAudioPlayback.BufferTargetMilliseconds}",
            $"url={channel.Url}");
        if (result.Started)
        {
            return;
        }

        _audioOpenTimer.Stop();
        await HandleFastMediaSorterAudioFailureAsync(channel, result.Error?.GetType().Name ?? "open_failed", result.StatusCode);
    }

    private void FastMediaSorterAudioPlayback_Playing(object? sender, FastMediaSorterAudioPlaybackEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (sender is not FastMediaSorterAudioPlayback playback || !IsCurrentFastMediaSorterPlayback(playback))
            {
                return;
            }

            _log.Event("FMS AUDIO LIVE",
                $"http={e.StatusCode?.ToString() ?? "n/a"}",
                $"open_to_playing_ms={e.OpenToPlaying.TotalMilliseconds:F0}",
                $"buffer_ms={FastMediaSorterAudioPlayback.BufferTargetMilliseconds}",
                $"url={_playingAudio?.Channel.Url ?? "n/a"}");
            await HandleAudioOpenedAsync();
        }), DispatcherPriority.Normal);
    }

    private void FastMediaSorterAudioPlayback_Ended(object? sender, FastMediaSorterAudioPlaybackEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (sender is FastMediaSorterAudioPlayback playback && IsCurrentFastMediaSorterPlayback(playback) && _playingAudio is { } row)
            {
                await HandleFastMediaSorterAudioEndedAsync(row.Channel, e.StatusCode);
            }
        }), DispatcherPriority.Normal);

    private void FastMediaSorterAudioPlayback_Failed(object? sender, FastMediaSorterAudioPlaybackEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (sender is FastMediaSorterAudioPlayback playback && IsCurrentFastMediaSorterPlayback(playback) && _playingAudio is { } row)
            {
                await HandleFastMediaSorterAudioFailureAsync(row.Channel, e.TransportError?.GetType().Name ?? "decoder_error", e.StatusCode);
            }
        }), DispatcherPriority.Normal);

    private async Task HandleFastMediaSorterAudioEndedAsync(StreamChannel channel, int? responseStatusCode)
    {
        _log.Event("FMS AUDIO ENDED", $"http={responseStatusCode?.ToString() ?? "n/a"}", $"url={channel.Url}");
        if (YieldToRandomStationHunt(channel, "end_reached"))
        {
            return;
        }

        StopFastMediaSorterAudioPlayback();
        // A phone's chunked body ending cleanly and a watch closing its socket both land here. Both mean
        // the device may have stopped, so the bounded StreamEnded budget decides, then names the stop.
        await RecoverAudioAsync(channel, "end_reached", endReached: true, firstResponseStatusCode: responseStatusCode,
            hasFirstResponseStatus: true, fastMediaSorterFailure: FastMediaSorterPlaybackFailureKind.Recoverable);
    }

    private async Task HandleFastMediaSorterAudioFailureAsync(StreamChannel channel, string reason, int? responseStatusCode)
    {
        _log.Event("FMS AUDIO FAIL", $"reason={reason}", $"http={responseStatusCode?.ToString() ?? "n/a"}", $"url={channel.Url}");
        if (YieldToRandomStationHunt(channel, reason))
        {
            return;
        }

        StopFastMediaSorterAudioPlayback();
        var failure = FastMediaSorterPlaybackFailure.Classify(responseStatusCode);
        if (failure == FastMediaSorterPlaybackFailureKind.ListenerLimit)
        {
            // Terminal on the first answer: every retry would itself be the fifth listener. The status
            // came from the leg's own request, so nothing was spent to learn it.
            _log.Event("AUDIO RECOVER", "trigger=ListenerLimit", "action=HardFail",
                $"http={responseStatusCode}", $"url={channel.Url}");
            await FailAudioTerminallyAsync(channel, reason, failure);
            return;
        }

        await RecoverAudioAsync(channel, reason, firstResponseStatusCode: responseStatusCode,
            hasFirstResponseStatus: true, fastMediaSorterFailure: failure);
    }

    private void StopFastMediaSorterAudioPlayback()
    {
        _fastMediaSorterAudioGeneration++;
        var playback = _fastMediaSorterAudioPlayback;
        _fastMediaSorterAudioPlayback = null;
        if (playback is not null)
        {
            playback.Playing -= FastMediaSorterAudioPlayback_Playing;
            playback.Ended -= FastMediaSorterAudioPlayback_Ended;
            playback.Failed -= FastMediaSorterAudioPlayback_Failed;
            playback.Dispose();
        }
    }

    private bool IsCurrentFastMediaSorterPlayback(FastMediaSorterAudioPlayback playback) =>
        ReferenceEquals(_fastMediaSorterAudioPlayback, playback) && _playingAudio is not null;

    private bool IsCurrentFastMediaSorterPlayback(FastMediaSorterAudioPlayback playback, int generation, StreamChannel channel) =>
        generation == _fastMediaSorterAudioGeneration &&
        ReferenceEquals(_fastMediaSorterAudioPlayback, playback) &&
        _playingAudio?.Channel.Id == channel.Id;
}
