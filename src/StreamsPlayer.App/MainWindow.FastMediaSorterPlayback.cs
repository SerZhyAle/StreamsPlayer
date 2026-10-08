using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private FastMediaSorterAudioPlayback? _fastMediaSorterAudioPlayback;
    private ExchangeTunnelForwarder? _fastMediaSorterAudioForwarder;
    private int _fastMediaSorterAudioGeneration;
    private int _fastMediaSorterAudioLegs;

    private static bool UsesFastMediaSorterAudioRoute(StreamChannel channel) =>
        channel.MediaKind == MediaKind.Audio && FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(channel);

    private void StartFastMediaSorterAudioPlayback(StreamChannel channel, bool reconnecting)
    {
        StopFastMediaSorterAudioPlayback();
        var playback = new FastMediaSorterAudioPlayback((tag, fields) => _log.Event(tag, fields))
        {
            AudioOutputDevice = _state.AudioOutputDevice,
            AudioChannelMode = _state.AudioChannelMode
        };
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
        var openToken = _audioRecoveryCts?.Token ?? CancellationToken.None;
        HandlerBoundary.Run(nameof(OpenFastMediaSorterAudioAsync),
            () => OpenFastMediaSorterAudioAsync(playback, generation, leg, channel, reconnecting, openToken));
    }

    private async Task OpenFastMediaSorterAudioAsync(
        FastMediaSorterAudioPlayback playback,
        int generation,
        int leg,
        StreamChannel channel,
        bool reconnecting,
        CancellationToken cancellationToken)
    {
        // SP-0203: the leg walks the descriptor's attempts in the producer's listed order, skipping a
        // transport it cannot open; a reconnect calls this path afresh, which is the restart at the
        // top. A row with no descriptor, or one stored before endpoints were listed, stays the single
        // attempt at its own address that it always was.
        var listed = channel.FastMediaSorterBroadcast?.PlaybackAttemptEndpoints();
        var attempts = listed is { Count: > 0 }
            ? listed
            : (IReadOnlyList<FastMediaSorterBroadcastEndpoint>)[new FastMediaSorterBroadcastEndpoint(channel.Url, "HTTP", null, null, null, null, null, null, null)];

        var volume = _pendingAudioVolume ?? (_stateCommitter?.Requested ?? _state).AudioVolume;
        FastMediaSorterAudioOpenResult result = new(null, TimeSpan.Zero, null, Cancelled: false);
        string reason = "unsupported_address";
        int playedAt = -1;
        for (var index = 0; index < attempts.Count; index++)
        {
            var endpoint = attempts[index];
            ExchangeTunnelForwarder? forwarder = null;
            Uri? address = null;
            if (endpoint is { Transport: "TUNNEL", Inner: { Length: > 0 } inner } &&
                ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnelUrl))
            {
                try
                {
                    forwarder = ExchangeTunnelForwarder.Start(tunnelUrl, inner, endpoint.CertFingerprint);
                    _fastMediaSorterAudioForwarder = forwarder;
                    if (LaunchableAddress.TryParseHttp(forwarder.LoopbackUrl, out var loopbackEndpoint))
                    {
                        address = loopbackEndpoint;
                    }
                }
                catch (Exception ex)
                {
                    _log.Event("FMS AUDIO TUNNEL ERROR", $"err={ex.Message}", $"url={channel.Url}");
                }
            }
            else if (LaunchableAddress.TryParseHttp(endpoint.Url, out var directEndpoint))
            {
                address = directEndpoint;
            }

            if (address is null)
            {
                forwarder?.Dispose();
                if (ReferenceEquals(_fastMediaSorterAudioForwarder, forwarder))
                {
                    _fastMediaSorterAudioForwarder = null;
                }

                LogFastMediaSorterAudioAttempt(leg, reconnecting, index, endpoint, "unsupported_address", result);
                continue;
            }

            // File 12 section 6.2's one number on record is the LAN connect bound; a relay or tunnel
            // attempt keeps the handler default behind the 15 s header budget, a direct http attempt
            // may not sit on connect past 1.5 s before the list moves on.
            var connectTimeout =
                address.Scheme == Uri.UriSchemeHttp && string.Equals(endpoint.Transport, "HTTP", StringComparison.OrdinalIgnoreCase)
                    ? FastMediaSorterPlaybackTransport.LanConnectTimeout
                    : (TimeSpan?)null;

            result = await playback.StartAsync(address, volume, cancellationToken, connectTimeout, endpoint.CertFingerprint);
            if (!IsCurrentFastMediaSorterPlayback(playback, generation, channel) || result.Cancelled)
            {
                playback.Dispose();
                forwarder?.Dispose();
                return;
            }

            if (result.Started)
            {
                playedAt = index;
                break;
            }

            reason = result.Error is not null ? result.Error.GetType().Name : "refused";
            LogFastMediaSorterAudioAttempt(leg, reconnecting, index, endpoint, reason, result);
            forwarder?.Dispose();
            if (ReferenceEquals(_fastMediaSorterAudioForwarder, forwarder))
            {
                _fastMediaSorterAudioForwarder = null;
            }
        }

        _log.Event("FMS AUDIO RESPONSE",
            $"leg={leg}",
            $"reconnecting={reconnecting}",
            $"attempt={playedAt}",
            $"http={result.StatusCode?.ToString() ?? "n/a"}",
            $"response_ms={result.ResponseElapsed.TotalMilliseconds:F0}",
            $"buffer_ms={FastMediaSorterAudioPlayback.BufferTargetMilliseconds}",
            $"url={channel.Url}");
        if (playedAt >= 0)
        {
            return;
        }

        _audioOpenTimer.Stop();
        // The leg's verdict is its last attempt's: a relay 503 behind a dead LAN address still means
        // the door itself answered, and the listener-limit rule reads that answer, not the LAN's.
        await HandleFastMediaSorterAudioFailureAsync(channel, reason, result.StatusCode);
    }

    private void LogFastMediaSorterAudioAttempt(
        int leg,
        bool reconnecting,
        int index,
        FastMediaSorterBroadcastEndpoint endpoint,
        string outcome,
        FastMediaSorterAudioOpenResult result) =>
        _log.Event("FMS AUDIO ATTEMPT",
            $"leg={leg}",
            $"reconnecting={reconnecting}",
            $"attempt={index}",
            $"transport={endpoint.Transport ?? "n/a"}",
            $"outcome={outcome}",
            $"http={result.StatusCode?.ToString() ?? "n/a"}",
            $"elapsed_ms={result.ResponseElapsed.TotalMilliseconds:F0}",
            // The sink redacts a relay address's broadcast id; the host and port stay for diagnosis.
            $"url={endpoint.Url}");

    private void FastMediaSorterAudioPlayback_Playing(object? sender, FastMediaSorterAudioPlaybackEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => HandlerBoundary.Run(nameof(FastMediaSorterAudioPlayback_Playing), async () =>
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
        })), DispatcherPriority.Normal);
    }

    private void FastMediaSorterAudioPlayback_Ended(object? sender, FastMediaSorterAudioPlaybackEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => HandlerBoundary.Run(nameof(FastMediaSorterAudioPlayback_Ended), async () =>
        {
            if (sender is FastMediaSorterAudioPlayback playback && IsCurrentFastMediaSorterPlayback(playback) && _playingAudio is { } row)
            {
                await HandleFastMediaSorterAudioEndedAsync(row.Channel, e.StatusCode);
            }
        })), DispatcherPriority.Normal);

    private void FastMediaSorterAudioPlayback_Failed(object? sender, FastMediaSorterAudioPlaybackEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => HandlerBoundary.Run(nameof(FastMediaSorterAudioPlayback_Failed), async () =>
        {
            if (sender is FastMediaSorterAudioPlayback playback && IsCurrentFastMediaSorterPlayback(playback) && _playingAudio is { } row)
            {
                await HandleFastMediaSorterAudioFailureAsync(row.Channel, e.TransportError?.GetType().Name ?? "decoder_error", e.StatusCode);
            }
        })), DispatcherPriority.Normal);

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
        var forwarder = _fastMediaSorterAudioForwarder;
        _fastMediaSorterAudioForwarder = null;
        forwarder?.Dispose();

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
