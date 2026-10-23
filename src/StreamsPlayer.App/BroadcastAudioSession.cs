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
/// session holds one station; this session keeps its own volume, stop and recovery while the shared audio
/// leg owns its engine, ordered endpoint attempts, transports and cancellation.
/// </summary>
internal sealed partial class BroadcastAudioSession : IDisposable
{
    private readonly StreamChannel _channel;
    private readonly BroadcastCredentialsSession? _credentials;
    private readonly Action<string, string[]>? _log;
    private readonly string? _outputDevice;
    private readonly AudioChannelMode _channelMode;
    private readonly object _gate = new();
    private LivePlaybackRecoveryPolicy _policy = new();
    private CancellationTokenSource? _sessionCancellation;
    private FastMediaSorterAudioLeg? _playback;
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
    internal string? LastFailureReason { get; private set; }

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
        FastMediaSorterAudioLeg playback;
        int generation;
        lock (_gate)
        {
            if (_disposed || token.IsCancellationRequested)
            {
                return;
            }

            StopLegLocked();
            playback = new FastMediaSorterAudioLeg(_log)
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
            playback.RelayFailed += failure => HandleFailure(playback, generation, token, failure, null);
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

    private async Task OpenAsync(FastMediaSorterAudioLeg playback, int generation, CancellationToken token)
    {
        try
        {
            var result = await playback.OpenAsync(_channel, _volume, _credentials?.Credentials, token,
                generation, State == BroadcastLegState.Reconnecting).ConfigureAwait(false);
            if (!result.Started && !result.Cancelled)
            {
                HandleFailure(playback, generation, token, result.Reason, result.StatusCode);
            }
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

        HandleFailure((FastMediaSorterAudioLeg)sender!, generation, token,
            e.TransportError?.GetType().Name ?? "decoder_error", e.StatusCode);
    }

    private void HandleFailure(FastMediaSorterAudioLeg playback, int generation, CancellationToken token, string reason, int? status)
    {
        if (!IsCurrent(playback, generation) || token.IsCancellationRequested)
        {
            return;
        }

        LastFailureReason = reason;

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

    private bool IsCurrent(FastMediaSorterAudioLeg playback, int generation)
    {
        lock (_gate)
        {
            return !_disposed && generation == _generation && ReferenceEquals(_playback, playback);
        }
    }

    private void StopLegLocked()
    {
        EndRecordingLocked();
        _generation++;
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

    private void SetState(BroadcastLegState state, string? messageKey)
    {
        State = state;
        MessageKey = messageKey;
        Changed?.Invoke();
    }
}
