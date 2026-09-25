using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0120: how a leg of playback starts and how a failed one gives back what it holds. Every per-leg reset runs
/// on the UI thread, where the monitors it resets are read; only the engine call of a re-open goes to a worker.
/// </summary>
public partial class PlayerWindow
{
    // The engine stop a terminal failure started; a hand retry opens only after it has landed.
    private Task _failureStop = Task.CompletedTask;

    /// <summary>
    /// Opens the channel on the UI thread - the first open, which has nothing on screen to keep responsive.
    /// <paramref name="qualityCeiling"/> is passed in rather than read here because the governor that owns the
    /// answer is single-threaded; the caller reads it on the UI thread, as late as it can (SP-0071).
    /// </summary>
    private void StartMedia(string reason, StreamQualityRung? qualityCeiling)
    {
        if (BeginLeg(reason, qualityCeiling) is { } cacheMs)
        {
            OpenLeg(cacheMs, qualityCeiling);
        }
    }

    /// <summary>
    /// The same open for a re-open: the leg starts here on the UI thread and only the engine call moves to a
    /// worker, because a flapping stream can hold <see cref="IVideoBackend.Play"/> for seconds and the backend
    /// serializes it against teardown. Must be called on the UI thread.
    /// </summary>
    private Task StartMediaOffUiThreadAsync(string reason, StreamQualityRung? qualityCeiling) =>
        BeginLeg(reason, qualityCeiling) is { } cacheMs
            ? Task.Run(() => OpenLeg(cacheMs, qualityCeiling))
            : Task.CompletedTask;

    /// <summary>
    /// Starts a leg: every per-leg reset, the leg count and the log line, and returns the live buffer the open
    /// must use - or null when the window is closing. UI thread only (SP-0120): the monitors reset here are read
    /// by the stats and watchdog ticks, and resetting them from the worker that opens the media let a tick
    /// landing between the resets see half a leg - a restarted open budget against the previous leg's clock -
    /// and fire a spurious open-timeout recovery.
    /// </summary>
    private uint? BeginLeg(string reason, StreamQualityRung? qualityCeiling)
    {
        if (_closing)
        {
            return null; // window is tearing down; do not touch the (soon) disposed player
        }

        _reachedLive = false;
        _isStalled = false;
        if (reason != "quality")
        {
            _probeLegPending = false; // SP-0130: only a quality re-open can carry a probe
        }

        _outcomeRecorded = false;
        // SP-0070: same reason as the health baseline below - the new media restarts the engine's
        // progress counters from zero, and differencing across that boundary would invent a freeze.
        _freeze.Reset();
        // SP-0096: one leg, one open budget. A re-open is entitled to its own, which is what makes the
        // budget a per-leg quantity rather than a session-wide one.
        _openBudget.Reset();
        _buffering = false;
        _playbackClock.Restart();
        _legCount++;
        // SP-0045: the new media restarts the engine's loss counters; drop the baseline so the reset is
        // not differenced into a fabricated disturbance. The health state itself is left alone - a
        // reconnect must stay red while it is in progress.
        NotifySignalHealthOpening();
        var cacheMs = reason == "initial" ? LiveCacheMilliseconds : ReconnectCacheMilliseconds;
        _liveCacheMs = cacheMs;
        _log.Event("PLAYBACK OPEN",
            $"reason={reason}",
            $"kind={_channel.MediaKind}",
            $"cache_ms={cacheMs}",
            $"engine={_backend.EngineName}",       // SP-0071: which engine received the ceiling below
            $"ceiling={Describe(qualityCeiling)}", // SP-0071: every leg says what it was opened with
            $"url={_channel.Url}");
        return cacheMs;
    }

    /// <summary>Hands the leg to the engine. Any thread; a rejection is reported on the UI thread.</summary>
    private void OpenLeg(uint cacheMs, StreamQualityRung? qualityCeiling)
    {
        if (_closing)
        {
            return;
        }

        // SP-0124: the catalog refuses an unlaunchable address before this window exists; this keeps the
        // window total on its own - such an address ends in the failure outcome, never in an exception.
        if (!LaunchableAddress.TryParse(_channel.Url, out var address))
        {
            ShowPlaybackFailure("unsupported_address");
            return;
        }

        if (!_backend.Play(address, cacheMs, rtspOverTcp: true, softwareDecode: true, qualityCeiling))
        {
            ShowPlaybackFailure("play_rejected");
        }
    }

    /// <summary>
    /// SP-0120: a verdict is final, so the window gives back what only a playing stream needs - the keep-awake
    /// hold, the freeze watchdog and the running engine. A startup-resume window fails without a dialog and
    /// used to keep the display on until the user happened to find it; a dead host also kept raising errors
    /// into an engine nobody was watching. Retry takes all three back (<see cref="RetryAfterFailureAsync"/>).
    /// </summary>
    private void ReleaseAfterFailure()
    {
        _watchdogTimer.Stop();
        _wake?.Dispose();
        _wake = null;
        _failureStop = _backend.StopPlaybackAsync();
        // SP-0121: a recording ends visibly with the playback it records, and its segments are saved and announced.
        EndRecordingAfterFailure(_failureStop);
    }

    /// <summary>
    /// The hand retry's open. It waits for the stop <see cref="ReleaseAfterFailure"/> started - the two are
    /// separate pool tasks, and an open that won the engine's gate first would be stopped by the verdict it
    /// is retrying. The stop's own outcome does not matter here: the engine logs a failed stop itself.
    /// </summary>
    private async Task RetryAfterFailureAsync()
    {
        await _failureStop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (_closing)
        {
            return;
        }

        // SP-0096: a hand retry is a new session, entitled to its own verdict. Cleared only now, so the stats tick
        // keeps treating the window as failed while the stop settles instead of judging the stopped leg.
        _failureShown = false;
        _wake ??= WakeGuard.Acquire(keepDisplayOn: true);
        _watchdogTimer.Start();
        await StartMediaOffUiThreadAsync("retry", QualityCeiling);
    }
}
