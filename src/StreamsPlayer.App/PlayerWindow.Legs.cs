using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0120: how a leg of playback starts and how a failed one gives back what it holds. Every per-leg reset runs
/// on the UI thread, where the monitors it resets are read; each engine call goes to a worker.
/// </summary>
public partial class PlayerWindow
{
    private static readonly TimeSpan OpenCallDeadline = TimeSpan.FromSeconds(20);

    private void AttachBackendEvents(IVideoBackend backend)
    {
        _bufferingHandler = cache => Backend_BufferingChanged(backend, cache);
        _errorHandler = () => Backend_EncounteredError(backend);
        _endHandler = () => Backend_EndReached(backend);
        _tracksHandler = () => Backend_TracksChanged(backend);
        _snapshotHandler = (id, frame) => Backend_SnapshotReady(backend, id, frame);
        backend.BufferingChanged += _bufferingHandler;
        backend.EncounteredError += _errorHandler;
        backend.EndReached += _endHandler;
        backend.TracksChanged += _tracksHandler;
        backend.SnapshotReady += _snapshotHandler;
    }

    private void DetachBackendEvents(IVideoBackend backend)
    {
        backend.BufferingChanged -= _bufferingHandler;
        backend.EncounteredError -= _errorHandler;
        backend.EndReached -= _endHandler;
        backend.TracksChanged -= _tracksHandler;
        backend.SnapshotReady -= _snapshotHandler;
    }

    private void ReplaceBackend()
    {
        // The old engine's callbacks keep their old instance even if they have already queued a UI action.
        var replacement = VideoBackendFactory.Create(_backendSelection, (int)VolumeSlider.Value, _isMuted, _log);
        replacement.AudioOutputDevice = _audioOutputDevice;
        replacement.AudioChannelMode = _audioChannelMode;
        var previous = _backend;
        try
        {
            previous.SetOverlay(null);
            VideoHost.Children.Remove(previous.View);
            VideoHost.Children.Add(replacement.View);
            replacement.SetOverlay(ControlsOverlay);
        }
        catch
        {
            replacement.SetOverlay(null);
            VideoHost.Children.Remove(replacement.View);
            if (!VideoHost.Children.Contains(previous.View))
            {
                VideoHost.Children.Add(previous.View);
            }

            previous.SetOverlay(ControlsOverlay);
            _retiredBackendReleases.Add(ReleaseRetiredBackendAsync(replacement));
            throw;
        }

        DetachBackendEvents(previous);
        _backend = replacement;
        AttachBackendEvents(replacement);
        replacement.RecordingInterrupted += Backend_RecordingInterrupted;
        if (_recordingSession is not null)
        {
            _recordingResumePending = true;
        }

        _previousBackendRelease = ReleaseRetiredBackendAsync(previous);
        _retiredBackendReleases.RemoveAll(task => task.IsCompleted);
        _retiredBackendReleases.Add(_previousBackendRelease);
    }

    private async Task ReleaseRetiredBackendAsync(IVideoBackend previous)
    {
        try
        {
            await previous.StopAndDisposeAsync();
        }
        catch (Exception exception)
        {
            _log.Event("PLAYBACK TEARDOWN", "stage=retired_backend", "ok=false", $"err={exception.Message}");
        }
        finally
        {
            previous.RecordingInterrupted -= Backend_RecordingInterrupted;
        }
    }

    /// <summary>
    /// Starts the leg on the UI thread and makes the engine call on a worker, with a deadline. A native
    /// <see cref="IVideoBackend.Play"/> can block while a source stops answering. The caller passes the
    /// quality ceiling it read on the UI thread, where the governor is owned (SP-0071).
    /// </summary>
    private async Task StartMediaOffUiThreadAsync(string reason, StreamQualityRung? qualityCeiling)
    {
        if (BeginLeg(reason, qualityCeiling) is not { } cacheMs)
        {
            return;
        }

        var backend = _backend;
        var open = Task.Run(() => OpenLeg(backend, cacheMs, qualityCeiling));
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        var deadline = Task.Delay(OpenCallDeadline, deadlineCts.Token);
        if (await Task.WhenAny(open, deadline) == open)
        {
            deadlineCts.Cancel();
            await open;
        }
        else if (!_closing && ReferenceEquals(backend, _backend))
        {
            _log.Event("PLAYBACK OPEN TIMEOUT", $"leg={_legCount}",
                $"after_ms={OpenCallDeadline.TotalMilliseconds:F0}", $"url={_channel.Url}");
            ShowPlaybackFailure("engine_open_timeout");
        }
    }

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

        if (_legCount > 0)
        {
            try
            {
                ReplaceBackend();
            }
            catch (Exception exception)
            {
                _log.Event("PLAYBACK OPEN", "ok=false", "stage=replace_backend", $"err={exception.Message}");
                ShowPlaybackFailure("engine_open_error");
                return null;
            }
        }

        _reachedLive = false;
        _isStalled = false;
        _snapshotRequests.Clear(); // a capture from replaced media cannot satisfy this leg's request
        if (reason != "quality")
        {
            _probeLegPending = false; // SP-0130: only a quality re-open can carry a probe
        }

        // SP-0070: same reason as the health baseline below - the new media restarts the engine's
        // progress counters from zero, and differencing across that boundary would invent a freeze.
        _freeze.Reset();
        // SP-0096: one leg, one open budget. A re-open is entitled to its own, which is what makes the
        // budget a per-leg quantity rather than a session-wide one.
        _openBudget.Reset();
        _buffering = false;
        _bufferFullPending = false;
        _frameShownLogged = false;
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
    private void OpenLeg(IVideoBackend backend, uint cacheMs, StreamQualityRung? qualityCeiling)
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

        try
        {
            if (!backend.Play(address, cacheMs, rtspOverTcp: true, softwareDecode: true, qualityCeiling))
            {
                ShowPlaybackFailure("play_rejected");
            }
        }
        catch (Exception exception)
        {
            _log.Event("PLAYBACK OPEN", "ok=false", $"err={exception.Message}", $"url={_channel.Url}");
            ShowPlaybackFailure("engine_open_error");
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
        var failureStop = _backend.StopPlaybackAsync();
        // SP-0121: a recording ends visibly with the playback it records, and its segments are saved and announced.
        EndRecordingAfterFailure(failureStop);
    }

    /// <summary>
    /// The hand retry gets a new backend, so an old native stop that never returns cannot hold it hostage.
    /// </summary>
    private async Task RetryAfterFailureAsync()
    {
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
