using System.Diagnostics;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>What one incoming broadcast's window is doing now (SP-0222 reading 5: the only live state a link has).</summary>
internal enum BroadcastLegState
{
    Connecting,
    Playing,
    Reconnecting,
    Stopped,
    Ended,
    Failed
}

/// <summary>
/// SP-0222 requirement 3: one audio-only broadcast listened to in a window of its own. The main window's audio
/// session holds one station and cannot hold two broadcasts or leave a station playing beside one, so an incoming
/// broadcast does not go through it: this session owns its engine, its volume, its stop and its recovery, and
/// touches no state of the main window. The walk over the descriptor's endpoints is the one the main window's
/// FastMediaSorter audio route makes - the producer's listed order, a tunnel through a loopback forwarder, the
/// pair answered only to a relay endpoint (SP-0215 requirement 7) - kept apart so the two sessions cannot hold
/// each other's generation, forwarder or credentials.
/// </summary>
internal sealed class BroadcastAudioSession : IDisposable
{
    private readonly StreamChannel _channel;
    private readonly BroadcastCredentialsSession? _credentials;
    private readonly Action<string, string[]>? _log;
    private readonly string? _outputDevice;
    private readonly AudioChannelMode _channelMode;
    private readonly object _gate = new();
    private LivePlaybackRecoveryPolicy _policy = new();
    private CancellationTokenSource? _sessionCancellation;
    private FastMediaSorterAudioPlayback? _playback;
    private ExchangeTunnelForwarder? _forwarder;
    private ExchangeRelayProxy? _relayProxy;
    private Stopwatch? _legPlayed;
    private int _generation;
    private int _volume;
    private bool _needsPasswordAgain;
    private bool _disposed;

    internal BroadcastAudioSession(
        StreamChannel channel,
        BroadcastCredentialsSession? credentials,
        int volume,
        string? outputDevice,
        AudioChannelMode channelMode,
        Action<string, string[]>? log)
    {
        _channel = channel;
        _credentials = credentials;
        _volume = Math.Clamp(volume, 0, 100);
        _outputDevice = outputDevice;
        _channelMode = channelMode;
        _log = log;
    }

    internal BroadcastLegState State { get; private set; } = BroadcastLegState.Stopped;

    /// <summary>The localization key of the message that ends a failed session, or null.</summary>
    internal string? MessageKey { get; private set; }

    /// <summary>Raised from whatever thread the change happened on; the window marshals it.</summary>
    internal event Action? Changed;

    internal int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            lock (_gate)
            {
                _playback?.SetVolume(_volume);
            }
        }
    }

    internal bool IsActive => State is BroadcastLegState.Connecting or BroadcastLegState.Playing or BroadcastLegState.Reconnecting;

    /// <summary>
    /// Starts listening. After a password refusal the first start asks the person once more (the stored password
    /// was the broadcaster's old one); every other start plays on what the session holds.
    /// </summary>
    internal void Start()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || IsActive)
            {
                return;
            }

            if (_needsPasswordAgain)
            {
                _needsPasswordAgain = false;
                _credentials?.ResolveForRetry(_channel.Id);
            }

            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            token = _sessionCancellation.Token;
            _policy = new LivePlaybackRecoveryPolicy();
        }

        BeginLeg(reconnecting: false, token);
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _sessionCancellation?.Cancel();
            StopLegLocked();
        }

        SetState(BroadcastLegState.Stopped, null);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sessionCancellation?.Cancel();
            StopLegLocked();
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
        }
    }

    private void BeginLeg(bool reconnecting, CancellationToken token)
    {
        FastMediaSorterAudioPlayback playback;
        int generation;
        lock (_gate)
        {
            if (_disposed || token.IsCancellationRequested)
            {
                return;
            }

            StopLegLocked();
            playback = new FastMediaSorterAudioPlayback(_log)
            {
                AudioOutputDevice = _outputDevice,
                AudioChannelMode = _channelMode
            };
            generation = ++_generation;
            _playback = playback;
            _legPlayed = null;
            playback.Playing += Playback_Playing;
            playback.Ended += Playback_Ended;
            playback.Failed += Playback_Failed;
        }

        SetState(reconnecting ? BroadcastLegState.Reconnecting : BroadcastLegState.Connecting, null);
        _ = OpenAsync(playback, generation, token);
    }

    /// <summary>
    /// SP-0225: the host and port of the first listed endpoint that names a network address, for the connection check
    /// of a leg that failed. Only the authority is taken - the path and the id behind it are the capability to
    /// listen and never leave this class.
    /// </summary>
    internal (string Host, int Port)? DiagnosisTarget()
    {
        foreach (var endpoint in _channel.FastMediaSorterBroadcast?.PlaybackAttemptEndpoints() ?? [])
        {
            if (ExchangeDiagnosisRoute.TryAuthorityOf(endpoint.Url, out var host, out var port))
            {
                return (host, port);
            }
        }

        return null;
    }

    private async Task OpenAsync(FastMediaSorterAudioPlayback playback, int generation, CancellationToken token)
    {
        try
        {
            var listed = _channel.FastMediaSorterBroadcast?.PlaybackAttemptEndpoints();
            var attempts = listed is { Count: > 0 }
                ? listed
                : (IReadOnlyList<FastMediaSorterBroadcastEndpoint>)[new FastMediaSorterBroadcastEndpoint(_channel.Url, "HTTP", null, null, null, null, null, null, null)];
            FastMediaSorterAudioOpenResult result = new(null, TimeSpan.Zero, null, Cancelled: false);
            var reason = "unsupported_address";
            for (var index = 0; index < attempts.Count; index++)
            {
                var endpoint = attempts[index];
                // SP-0226 / items DK and DP: an exchange endpoint plays only sealed, with the keys its box or its link
                // handed over; the refusal is the leg's verdict and is said once, in words.
                var decision = ExchangeBroadcastKeys.Decide(endpoint);
                if (!decision.Allowed)
                {
                    reason = decision.Reason;
                    _log?.Invoke("FMS AUDIO ATTEMPT", [$"attempt={index}", $"outcome={reason}"]);
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
                        lock (_gate)
                        {
                            if (IsCurrent(playback, generation))
                            {
                                _forwarder = forwarder;
                            }
                        }

                        if (LaunchableAddress.TryParseHttp(forwarder.LoopbackUrl, out var loopback))
                        {
                            address = loopback;
                        }
                    }
                    catch (ExchangeBroadcastCapacityException)
                    {
                        // The local listeners every incoming broadcast shares are all taken: the person is told the
                        // bound, and no other endpoint of this leg is worth a second try for the same reason.
                        reason = ExchangeBroadcastResources.CapacityReason;
                        _log?.Invoke("FMS AUDIO TUNNEL ERROR", ["reason=capacity"]);
                        break;
                    }
                    catch (Exception exception)
                    {
                        _log?.Invoke("FMS AUDIO TUNNEL ERROR", [$"err={exception.GetType().Name}"]);
                    }
                }
                else if (decision.RelayOptions(endpoint.Url) is { } sealedRelay)
                {
                    // Item DL: the sealed relay is opened by the loopback proxy; the leg reads clear media from it.
                    try
                    {
                        relayProxy = ExchangeRelayProxy.Start(endpoint.Url, endpoint.CertFingerprint,
                            credentials: decision.Credentials ?? _credentials?.Credentials, sealedRelay: sealedRelay);
                        lock (_gate)
                        {
                            if (IsCurrent(playback, generation))
                            {
                                _relayProxy = relayProxy;
                            }
                        }

                        if (LaunchableAddress.TryParseHttp(relayProxy.LoopbackUrl, out var proxyAddress))
                        {
                            address = proxyAddress;
                        }
                    }
                    catch (ExchangeBroadcastCapacityException)
                    {
                        reason = ExchangeBroadcastResources.CapacityReason;
                        _log?.Invoke("FMS AUDIO RELAY ERROR", ["reason=capacity"]);
                        break;
                    }
                    catch (Exception exception)
                    {
                        _log?.Invoke("FMS AUDIO RELAY ERROR", [$"err={exception.GetType().Name}"]);
                    }
                }
                else if (LaunchableAddress.TryParseHttp(endpoint.Url, out var direct))
                {
                    address = direct;
                }

                if (address is null)
                {
                    ReleaseForwarder(forwarder);
                    ReleaseRelayProxy(relayProxy);
                    _log?.Invoke("FMS AUDIO ATTEMPT", [$"attempt={index}", "outcome=unsupported_address"]);
                    continue;
                }

                // File 12 section 6.2's one number on record is the LAN connect bound; a relay or tunnel attempt keeps
                // the handler default behind the 15 s header budget.
                var connectTimeout =
                    address.Scheme == Uri.UriSchemeHttp && string.Equals(endpoint.Transport, "HTTP", StringComparison.OrdinalIgnoreCase)
                        ? FastMediaSorterPlaybackTransport.LanConnectTimeout
                        : (TimeSpan?)null;

                // SP-0215 requirement 7: only the relay endpoint sees the pair - a direct LAN address and a tunnel
                // inner address are the producer's own servers, and no pair rides a leg that did not ask.
                var relayCredentials = relayProxy is null && endpoint.Transport?.Equals("RELAY", StringComparison.OrdinalIgnoreCase) == true
                    ? _credentials?.Credentials
                    : null;
                result = await playback.StartAsync(address, _volume, token, connectTimeout,
                        relayProxy is null ? endpoint.CertFingerprint : null, relayCredentials)
                    .ConfigureAwait(false);
                if (!IsCurrent(playback, generation) || result.Cancelled)
                {
                    playback.Dispose();
                    ReleaseForwarder(forwarder);
                    ReleaseRelayProxy(relayProxy);
                    return;
                }

                if (result.Started)
                {
                    // A record that fails once the leg is live ends it, as an open that fails does; before that the open
                    // loop reads the proxy's verdict itself and may still try the next endpoint.
                    if (relayProxy is not null)
                    {
                        relayProxy.Failed += failure => HandleFailure(playback, generation, token, failure, null);
                    }

                    return;
                }

                reason = forwarder?.LastRefusalReason
                    ?? relayProxy?.LastFailure
                    ?? (result.Error is ExchangeBroadcastCertificateException certificate ? certificate.Reason
                        : result.Error is not null ? result.Error.GetType().Name : "refused");
                if (result.StatusCode == 401 && channelIsProtected())
                {
                    reason = relayCredentials is null
                        ? ExchangeBroadcastAuth.PasswordRequiredReason
                        : ExchangeBroadcastAuth.PasswordRefusedReason;
                }

                _log?.Invoke("FMS AUDIO ATTEMPT", [$"attempt={index}", $"transport={endpoint.Transport ?? "n/a"}",
                    $"outcome={reason}", $"http={result.StatusCode?.ToString() ?? "n/a"}"]);
                ReleaseForwarder(forwarder);
                ReleaseRelayProxy(relayProxy);
            }

            HandleFailure(playback, generation, token, reason, result.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // Stop or Dispose ended the leg while its request was out; the state was set by whoever stopped it.
        }
        catch (Exception exception)
        {
            // A leg that faulted for a reason nobody classified ends the session and says so: a task nobody awaits
            // must not fail where nobody looks. Only the type reaches the log.
            _log?.Invoke("FMS AUDIO FAULT", [$"err={exception.GetType().Name}"]);
            if (IsCurrent(playback, generation))
            {
                End(BroadcastLegState.Failed, IncomingBroadcastFailure.NoLongerAvailableKey);
            }
        }

        bool channelIsProtected() => _channel.FastMediaSorterBroadcast?.Auth is { } auth &&
            string.Equals(auth.Scheme, ExchangeBroadcastAuth.SupportedScheme, StringComparison.Ordinal);
    }

    private void Playback_Playing(object? sender, FastMediaSorterAudioPlaybackEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _playback))
            {
                return;
            }

            _legPlayed = Stopwatch.StartNew();
        }

        SetState(BroadcastLegState.Playing, null);
    }

    private void Playback_Ended(object? sender, FastMediaSorterAudioPlaybackEventArgs e)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _playback) || _sessionCancellation is null)
            {
                return;
            }

            token = _sessionCancellation.Token;
        }

        // A phone's chunked body ending cleanly and a watch closing its socket both land here. Both mean the
        // device may have stopped, so the bounded budget decides, then names the stop.
        Recover(token, "end_reached", endReached: true, e.StatusCode);
    }

    private void Playback_Failed(object? sender, FastMediaSorterAudioPlaybackEventArgs e)
    {
        int generation;
        CancellationToken token;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _playback) || _sessionCancellation is null)
            {
                return;
            }

            generation = _generation;
            token = _sessionCancellation.Token;
        }

        HandleFailure((FastMediaSorterAudioPlayback)sender!, generation, token,
            e.TransportError?.GetType().Name ?? "decoder_error", e.StatusCode);
    }

    private void HandleFailure(FastMediaSorterAudioPlayback playback, int generation, CancellationToken token, string reason, int? status)
    {
        if (!IsCurrent(playback, generation) || token.IsCancellationRequested)
        {
            return;
        }

        // The refusals no reconnect can mend (a refused password, a certificate that does not match, the bound, a
        // full listener list) end the leg at once with their message; everything else spends the recovery budget.
        if (IncomingBroadcastFailure.TerminalMessageKey(reason, status) is { } terminalKey)
        {
            if (IncomingBroadcastFailure.IsPasswordRefusal(reason))
            {
                // The password the relay refused is the broadcaster's old one: forgotten, so the next play is one new prompt.
                _credentials?.NoteRefused(_channel.Id);
                _needsPasswordAgain = true;
            }

            End(BroadcastLegState.Failed, terminalKey);
            return;
        }

        Recover(token, reason, endReached: false, status);
    }

    private void Recover(CancellationToken token, string reason, bool endReached, int? status)
    {
        TimeSpan played;
        RecoveryDecision decision;
        lock (_gate)
        {
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            played = _legPlayed?.Elapsed ?? TimeSpan.Zero;
            StopLegLocked();
            _policy.NotifyLegPlayed(played);
            decision = _policy.Decide(new PlaybackFailureSignal(reason, HttpStatusCode: status, EndReached: endReached));
        }

        _log?.Invoke("FMS AUDIO RECOVER", [$"trigger={decision.Trigger}", $"action={decision.Kind}",
            $"attempt={decision.Attempt}", $"budget={decision.Budget}", $"reason={reason}"]);
        if (decision.Kind == RecoveryActionKind.HardFail)
        {
            End(endReached ? BroadcastLegState.Ended : BroadcastLegState.Failed, IncomingBroadcastFailure.NoLongerAvailableKey);
            return;
        }

        SetState(BroadcastLegState.Reconnecting, null);
        _ = ReconnectAfterAsync(decision.Delay, token);
    }

    private async Task ReconnectAfterAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // stop or close cancelled the wait - never restart a stopped session
        }

        BeginLeg(reconnecting: true, token);
    }

    private void End(BroadcastLegState state, string? messageKey)
    {
        lock (_gate)
        {
            StopLegLocked();
        }

        SetState(state, messageKey);
    }

    private bool IsCurrent(FastMediaSorterAudioPlayback playback, int generation)
    {
        lock (_gate)
        {
            return !_disposed && generation == _generation && ReferenceEquals(_playback, playback);
        }
    }

    private void StopLegLocked()
    {
        _generation++;
        var forwarder = _forwarder;
        _forwarder = null;
        DisposeForwarder(forwarder);
        var relayProxy = _relayProxy;
        _relayProxy = null;
        relayProxy?.Dispose();

        var playback = _playback;
        _playback = null;
        if (playback is not null)
        {
            playback.Playing -= Playback_Playing;
            playback.Ended -= Playback_Ended;
            playback.Failed -= Playback_Failed;
            playback.Dispose();
        }
    }

    private static void DisposeForwarder(ExchangeTunnelForwarder? forwarder) => forwarder?.Dispose();

    /// <summary>A relay proxy an attempt is done with: forgotten by the leg as well as closed.</summary>
    private void ReleaseRelayProxy(ExchangeRelayProxy? relayProxy)
    {
        if (relayProxy is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_relayProxy, relayProxy))
            {
                _relayProxy = null;
            }
        }

        relayProxy.Dispose();
    }

    /// <summary>A forwarder an attempt is done with: forgotten by the leg as well as closed, so the stop of the leg does not close it twice.</summary>
    private void ReleaseForwarder(ExchangeTunnelForwarder? forwarder)
    {
        if (forwarder is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_forwarder, forwarder))
            {
                _forwarder = null;
            }
        }

        forwarder.Dispose();
    }

    private void SetState(BroadcastLegState state, string? messageKey)
    {
        State = state;
        MessageKey = messageKey;
        Changed?.Invoke();
    }
}
