using System.Diagnostics;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0203: the attempts of a leg - the producer's ordered endpoint list, the move from one endpoint to
/// the next, and the per-attempt resource and token that keep a superseded attempt from acting on the
/// one that replaced it. Split out of <c>PlayerWindow.Legs.cs</c> to keep that file inside the size budget.
/// </summary>
public partial class PlayerWindow
{
    /// <summary>
    /// SP-0203: file 12 section 6.2's 1.5 s connect bound cannot be seen from inside the engine, so
    /// the bytes-based dead-source slice is its honest proxy. A LAN attempt gets this short slice and
    /// the list moves on; an exchange attempt keeps the measured 8 s default - the relay is the
    /// endpoint expected to answer.
    /// </summary>
    private static readonly TimeSpan LanAttemptSlice = TimeSpan.FromSeconds(4);

    private IReadOnlyList<FastMediaSorterBroadcastEndpoint> _attemptPlan = [];
    private int _attemptIndex;
    private ExchangeTunnelForwarder? _tunnelForwarder;
    private ExchangeRelayProxy? _relayProxy;
    private readonly Stopwatch _attemptClock = Stopwatch.StartNew();

    // Every attempt of a leg shares one backend, so the backend's identity cannot tell an answer from a
    // superseded attempt from one for the current attempt. The epoch can: it moves whenever an attempt is
    // retired, workers carry the value they were started with, and the gate makes "is it still current"
    // and "take the resource" one step against the UI thread's disposal.
    private readonly object _attemptResourceGate = new();
    private int _attemptEpoch;
    // True while this leg's own open call is outstanding - the window in which a recovery that started
    // the leg is no longer deciding and a failed first attempt may still move to the next endpoint.
    private bool _legOpenInFlight;

    /// <summary>
    /// SP-0203: the attempt failed before it went live - a rejected open, an engine error, a
    /// dead-source slice or the leg deadline - and the producer's list has another endpoint. The
    /// same backend re-opens on the next attempt's address with that endpoint's own buffer and
    /// slice; the leg keeps its identity, and recovery is never spent on a transport the producer
    /// ranked behind a working one.
    /// </summary>
    private async Task AdvanceAttemptAsync(IVideoBackend backend, string reason)
    {
        var epoch = RetireAttempt();
        _attemptIndex++;
        var endpoint = _attemptPlan[_attemptIndex];
        // SP-0096/SP-0208: the new media restarts the engine's counters, so the freeze baseline, the
        // open budget and the attempt clock all start over with this endpoint's rules.
        _freeze.Reset();
        _openBudget = new PlaybackOpenBudget(AttemptSlice(endpoint, hasSuccessor: true));
        _attemptClock.Restart();
        _firstByteLogged = false;
        _buffering = false;
        _bufferFullPending = false;
        var cacheMs = BroadcastLiveCache.For(_channel, endpoint) ?? LiveCacheMilliseconds;
        _liveCacheMs = cacheMs;
        _log.Event("PLAYBACK ATTEMPT",
            $"attempt={_attemptIndex}/{_attemptPlan.Count}",
            $"transport={endpoint.Transport ?? "n/a"}",
            $"reason={reason}",
            $"cache_ms={cacheMs}",
            // The sink redacts a relay address's broadcast id; the transport and host stay useful.
            $"url={endpoint.Url}");

        await OpenWithDeadlineAsync(backend, epoch, cacheMs, QualityCeiling);
    }

    /// <summary>
    /// Whether a pre-live failure may still move to the producer's next endpoint. Only a leg that has not
    /// shown its picture qualifies - after that the way back is the recovery policy, which restarts the
    /// list from the top (<see cref="BroadcastAttemptAdvance"/>).
    /// </summary>
    private bool CanAdvanceAttempt() =>
        BroadcastAttemptAdvance.CanAdvance(
            closing: _closing,
            failureShown: _failureShown,
            probeLegPending: _probeLegPending,
            reachedLive: _reachedLive,
            recoveryInFlight: _recoveryInFlight,
            legOpenInFlight: _legOpenInFlight,
            attemptIndex: _attemptIndex,
            attemptCount: _attemptPlan.Count);

    /// <summary>The address the current attempt opens - for the log line, redacted by the sink.</summary>
    private string AttemptUrl() =>
        _attemptIndex < _attemptPlan.Count ? _attemptPlan[_attemptIndex].Url : _channel.Url;

    /// <summary>The per-attempt resource - a tunnel forwarder or a relay pin proxy - belongs to the attempt, not the leg.</summary>
    private void DisposeAttemptResource(int? onlyIfCurrentEpoch = null)
    {
        lock (_attemptResourceGate)
        {
            if (onlyIfCurrentEpoch is { } epoch && epoch != _attemptEpoch)
            {
                return; // a worker's cleanup for an attempt that is gone must not take the next one's resource
            }

            _tunnelForwarder?.Dispose();
            _tunnelForwarder = null;
            _relayProxy?.Dispose();
            _relayProxy = null;
        }
    }

    /// <summary>Gives back the attempt's resource and starts a new epoch; the returned value is the new attempt's token.</summary>
    private int RetireAttempt()
    {
        lock (_attemptResourceGate)
        {
            DisposeAttemptResource();
            return ++_attemptEpoch;
        }
    }

    /// <summary>
    /// A worker's claim on the attempt's resource. Refused - and the caller disposes what it built - when
    /// the attempt was retired while the resource was being started.
    /// </summary>
    private bool TryHoldAttemptResource(int epoch, Action hold)
    {
        lock (_attemptResourceGate)
        {
            if (epoch != _attemptEpoch || _closing)
            {
                return false;
            }

            hold();
            return true;
        }
    }

    /// <summary>
    /// The attempt's dead-source slice: the short LAN bound exists to hand the list to the next
    /// endpoint, so a last attempt - and any single-attempt channel, catalog rows included - keeps
    /// the measured eight seconds SP-0096 chose against 311 openings.
    /// </summary>
    private static TimeSpan AttemptSlice(FastMediaSorterBroadcastEndpoint endpoint, bool hasSuccessor)
    {
        if (!hasSuccessor)
        {
            return PlaybackOpenBudget.DeadSourceAfter;
        }

        return endpoint.Transport is not null &&
            (endpoint.Transport.Equals("RELAY", StringComparison.OrdinalIgnoreCase) ||
             endpoint.Transport.Equals("TUNNEL", StringComparison.OrdinalIgnoreCase))
            ? PlaybackOpenBudget.DeadSourceAfter
            : LanAttemptSlice;
    }

    private static FastMediaSorterBroadcastEndpoint SingleAttempt(StreamChannel channel) =>
        new(
            channel.Url,
            channel.Url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) ? "RTSP" : "HTTP",
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    /// <summary>
    /// SP-0184: a refusal from <paramref name="backend"/> is a verdict on that engine's leg. The call can return
    /// after a newer leg replaced the engine - the replaced one is stopped, so it refuses or throws - and that
    /// stale answer must not fail the live leg, the same guard the open-deadline verdict applies. The comparison
    /// is made on the UI thread, where <c>_backend</c> is replaced. SP-0203: while the producer's list has
    /// another endpoint, the refusal moves the attempt forward instead of ending the leg. The attempts of
    /// one leg share the engine, so <paramref name="epoch"/> is what says the refusal is for the attempt
    /// that is still open and not for one the leg already moved past.
    /// </summary>
    private void ReportOpenFailure(IVideoBackend backend, int epoch, string reason)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportOpenFailure(backend, epoch, reason));
            return;
        }

        if (!ReferenceEquals(backend, _backend) || epoch != _attemptEpoch)
        {
            _log.Event("PLAYBACK FAIL IGNORED", $"reason={reason}",
                ReferenceEquals(backend, _backend) ? "why=superseded_attempt" : "why=superseded_leg",
                $"url={_channel.Url}");
            return;
        }

        if (CanAdvanceAttempt())
        {
            _ = AdvanceAttemptAsync(backend, reason);
            return;
        }

        ShowPlaybackFailure(reason);
    }
}
