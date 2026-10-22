using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private FastMediaSorterAudioPlayback? _fastMediaSorterAudioPlayback;
    private ExchangeTunnelForwarder? _fastMediaSorterAudioForwarder;
    private ExchangeRelayProxy? _fastMediaSorterAudioRelayProxy;
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
        var streaming = new bool[1]; // set once the leg is live: a sealed relay failing after that is a verdict, not an open failure
        for (var index = 0; index < attempts.Count; index++)
        {
            var endpoint = attempts[index];
            // SP-0226 / items DK and DP: an exchange endpoint plays only sealed, with the keys its box or its link handed
            // over - never as clear bytes through a server. The refusal is the leg's verdict, said once in words.
            var decision = ExchangeBroadcastKeys.Decide(endpoint);
            if (!decision.Allowed)
            {
                reason = decision.Reason;
                LogFastMediaSorterAudioAttempt(leg, reconnecting, index, endpoint, reason, result);
                continue;
            }

            ExchangeTunnelForwarder? forwarder = null;
            ExchangeRelayProxy? relayProxy = null;
            Uri? address = null;
            if (endpoint is { Transport: "TUNNEL", Inner: { Length: > 0 } inner } &&
                ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnelUrl))
            {
                try
                {
                    forwarder = ExchangeTunnelForwarder.Start(tunnelUrl, inner, endpoint.CertFingerprint, tunnelPin: decision.TunnelPin);
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
            else if (decision.RelayOptions(endpoint.Url) is { } sealedRelay)
            {
                // Item DL: a sealed relay is records, not an ordinary stream; the loopback proxy opens them and the
                // audio route reads clear media from it. The pair, if there is one, rides the proxy's own connection.
                try
                {
                    relayProxy = ExchangeRelayProxy.Start(endpoint.Url, endpoint.CertFingerprint,
                        credentials: decision.Credentials ?? _playingBroadcastCredentials, sealedRelay: sealedRelay);
                    _fastMediaSorterAudioRelayProxy = relayProxy;
                    relayProxy.Failed += failure => Dispatcher.BeginInvoke(new Action(() => HandlerBoundary.Run(
                        nameof(OpenFastMediaSorterAudioAsync), async () =>
                        {
                            if (streaming[0] && IsCurrentFastMediaSorterPlayback(playback, generation, channel))
                            {
                                await HandleFastMediaSorterAudioFailureAsync(channel, failure, null);
                            }
                        })), DispatcherPriority.Normal);
                    if (LaunchableAddress.TryParseHttp(relayProxy.LoopbackUrl, out var proxyEndpoint))
                    {
                        address = proxyEndpoint;
                    }
                }
                catch (Exception ex)
                {
                    _log.Event("FMS AUDIO RELAY ERROR", $"err={ex.GetType().Name}", $"url={channel.Url}");
                }
            }
            else if (LaunchableAddress.TryParseHttp(endpoint.Url, out var directEndpoint))
            {
                address = directEndpoint;
            }

            if (address is null)
            {
                forwarder?.Dispose();
                relayProxy?.Dispose();
                if (ReferenceEquals(_fastMediaSorterAudioForwarder, forwarder))
                {
                    _fastMediaSorterAudioForwarder = null;
                }
                if (ReferenceEquals(_fastMediaSorterAudioRelayProxy, relayProxy))
                {
                    _fastMediaSorterAudioRelayProxy = null;
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

            // SP-0215 requirement 7: only the relay endpoint sees the pair - a direct LAN address and a
            // tunnel inner address are the producer's own servers, and no pair rides a leg that did not ask.
            // A sealed relay is reached through the proxy over plain loopback: the pin and the pair are the proxy's.
            var relayCredentials = relayProxy is null && endpoint.Transport?.Equals("RELAY", StringComparison.OrdinalIgnoreCase) == true
                ? _playingBroadcastCredentials
                : null;
            result = await playback.StartAsync(address, volume, cancellationToken, connectTimeout,
                relayProxy is null ? endpoint.CertFingerprint : null, relayCredentials);
            if (!IsCurrentFastMediaSorterPlayback(playback, generation, channel) || result.Cancelled)
            {
                playback.Dispose();
                forwarder?.Dispose();
                relayProxy?.Dispose();
                return;
            }

            if (result.Started)
            {
                streaming[0] = true;
                playedAt = index;
                break;
            }

            reason = forwarder?.LastRefusalReason
                ?? relayProxy?.LastFailure
                ?? (result.Error is ExchangeBroadcastCertificateException certificate ? certificate.Reason
                    : result.Error is not null ? result.Error.GetType().Name : "refused");
            if (result.StatusCode == 401 && IsPasswordProtected(channel))
            {
                reason = relayCredentials is null
                    ? ExchangeBroadcastAuth.PasswordRequiredReason
                    : ExchangeBroadcastAuth.PasswordRefusedReason;
            }
            LogFastMediaSorterAudioAttempt(leg, reconnecting, index, endpoint, reason, result);
            forwarder?.Dispose();
            relayProxy?.Dispose();
            if (ReferenceEquals(_fastMediaSorterAudioForwarder, forwarder))
            {
                _fastMediaSorterAudioForwarder = null;
            }
            if (ReferenceEquals(_fastMediaSorterAudioRelayProxy, relayProxy))
            {
                _fastMediaSorterAudioRelayProxy = null;
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
        if (ExchangeBroadcastAuth.MessageKey(reason) is not null)
        {
            // One message (below); the stored password the relay refused is the broadcaster's old
            // one, so it is forgotten here and the next explicit play or Retry is the one new prompt.
            ForgetBroadcastPassword(channel.Id);
            _playingBroadcastCredentials = null;
            await FailAudioTerminallyAsync(channel, reason);
            return;
        }
        if (ExchangeBroadcastCertificateException.MessageKey(reason) is not null)
        {
            await FailAudioTerminallyAsync(channel, reason);
            return;
        }
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
        var relayProxy = _fastMediaSorterAudioRelayProxy;
        _fastMediaSorterAudioRelayProxy = null;
        relayProxy?.Dispose();

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
