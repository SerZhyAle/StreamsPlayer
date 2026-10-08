using System.IO;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0101 / SP-0121: recording the broadcast this window plays. A recording is of the channel, not of one
/// connection to it (decision 1a): the engine ends its segment on every re-open, the session keeps each one and
/// finishes it in the background, and the next segment starts once the picture is back. The badge is dimmed while
/// no segment is being written, so it never claims a recording that is not happening (R1), and when the recording
/// ends - by the user, with the playback, or because a segment could not be resumed - every segment is reported
/// together, including a file that could not be moved and where it was left (R2).
/// <para>Every start, stop and resume goes through <see cref="_recordingBusy"/> on the UI thread, so at most one of
/// them is ever waiting on the engine; the engine does the native work off the UI thread (R3).</para>
/// </summary>
public partial class PlayerWindow
{
    /// <summary>Consecutive resumes the engine refused while the picture was live before the recording ends.</summary>
    private const int MaxResumeFailures = 3;

    /// <summary>How long a recording notice stays up: it may name a folder or the place a file was left.</summary>
    private const int RecordingNoticeHoldMs = 5000;

    // Volatile: the engine reads it from the thread that ended a segment (Backend_RecordingInterrupted).
    private volatile VideoRecordingSession? _recordingSession;
    private bool _recordingBusy;
    private bool _recordingEnding;
    private bool _recordingResumePending;
    private int _recordingResumeFailures;

    private enum RecordingEnd
    {
        User,
        PlaybackFailed,
        ResumeFailed,
        Smoke,
        // SP-0178: the window closed with a session running - logged as such, not as a user stop.
        WindowClosed
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(RecordButton_Click), ToggleRecordingAsync);

    private Task ToggleRecordingAsync() => _recordingSession is null
        ? StartRecordingSessionAsync()
        : EndRecordingSessionAsync(RecordingEnd.User);

    private async Task StartRecordingSessionAsync()
    {
        if (_recordingBusy || _closing || _failureShown || !_reachedLive || _backend.RecordUnavailableReason is not null)
        {
            return;
        }

        // SP-0179: the smoke gate's folder is a chain of one - its check reads the file from exactly there.
        IReadOnlyList<string> chain = SmokeRecording.Folder is { } smokeFolder && _smokeRecordingActive
            ? new[] { smokeFolder }
            : CaptureFolders.Chain(CaptureKind.StreamVideo, _captureFolder(CaptureKind.StreamVideo));
        _recordingBusy = true;
        try
        {
            // Probed off the UI thread: a folder on a sleeping or unplugged drive can take seconds to refuse.
            var destination = await Task.Run(() => CaptureFolders.FirstWritable(chain));
            if (_closing)
            {
                return;
            }

            if (_failureShown || !_reachedLive)
            {
                return; // the source changed while the folder probe was in flight
            }

            if (destination is null)
            {
                _log.Event("RECORD SESSION", "state=no_writable_folder", $"engine={_backend.EngineName}", $"folder={chain[0]}", $"url={_channel.Url}");
                ShowFrameToast(LocalizationService.Format("RecordWriteFailed", chain[0]), RecordingNoticeHoldMs);
                return;
            }

            var target = new RecordingTarget(destination.Folder, StreamTitleFormatter.Display(_channel.Title), destination.Rest);
            var session = new VideoRecordingSession(target, _log);
            // Set before the engine is asked, so a re-open that ends the first segment while the start is in flight
            // finds the session to hand it to.
            _recordingSession = session;
            var backend = _backend;
            bool started;
            try
            {
                started = await backend.StartRecordingAsync(target);
            }
            catch
            {
                // SP-0184: the engine answers a refusal with false; an exception is unexpected, and the session set
                // above would otherwise outlive it with no segment behind it - a badge claiming a recording nothing
                // writes. A session that already took a segment from a re-open stays, owed a resume.
                if (ReferenceEquals(_recordingSession, session))
                {
                    if (session.SegmentCount == 0)
                    {
                        _recordingSession = null;
                    }
                    else
                    {
                        _recordingResumePending = true;
                    }
                }

                throw;
            }

            if (!ReferenceEquals(backend, _backend))
            {
                _recordingResumePending = true;
                return; // the old leg's segment is collected by its release; resume on the current one
            }

            if (!started)
            {
                _recordingSession = null;
                _log.Event("RECORD SESSION", "state=start_failed", $"engine={_backend.EngineName}", $"url={_channel.Url}");
                if (!_closing)
                {
                    ShowFrameToast(LocalizationService.Get("RecordStartFailed"));
                }

                return;
            }

            _recordingResumePending = false;
            _recordingResumeFailures = 0;
            _log.Event("RECORD SESSION", "state=started", $"engine={_backend.EngineName}", $"folder={target.Folder}", $"skipped={destination.Skipped ?? "none"}", $"url={_channel.Url}");
            if (destination.Skipped is { } skipped && !_closing)
            {
                // CAPTURE-OUTPUT rule 11: the fallback is told when it happens, not when the recording ends.
                ShowFrameToast(LocalizationService.Format("RecordFellBack", skipped, destination.Folder), RecordingNoticeHoldMs);
            }
        }
        finally
        {
            _recordingBusy = false;
            if (!_closing)
            {
                UpdateRecordingUi();
            }
        }
    }

    /// <summary>
    /// The engine ended a segment on its own - a re-open, a stop, teardown. Engine thread, inside its gate: take
    /// the segment and leave. A segment with no session to receive it (a race with the end of one) is still
    /// finished, so no file is ever abandoned in staging.
    /// </summary>
    private void Backend_RecordingInterrupted(RecordingSegment segment)
    {
        var session = _recordingSession;
        if (session is null)
        {
            _ = RecordingFinisher.FinishAsync(segment, _log);
            return;
        }

        session.Add(segment);
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !ReferenceEquals(_recordingSession, session) || _recordingEnding)
            {
                return;
            }

            _recordingResumePending = true;
            _log.Event("RECORD SESSION", "state=between_segments", $"segments={session.SegmentCount}", $"url={_channel.Url}");
            UpdateRecordingUi();
        });
    }

    /// <summary>The picture is back: start the next segment if one is owed. UI thread.</summary>
    private void ResumeRecordingIfPending()
    {
        if (_recordingResumePending)
        {
            HandlerBoundary.Run(nameof(ResumeRecordingIfPending), ResumeRecordingAsync);
        }
    }

    private async Task ResumeRecordingAsync()
    {
        if (_recordingSession is not { } session || !_recordingResumePending || !_previousBackendRelease.IsCompleted
            || _recordingBusy || _recordingEnding
            || _closing || _failureShown || !_reachedLive || _buffering)
        {
            return;
        }

        _recordingBusy = true;
        bool started;
        try
        {
            var backend = _backend;
            started = await backend.StartRecordingAsync(session.Target);
            if (!ReferenceEquals(backend, _backend))
            {
                return; // a newer leg still owes the resume
            }
        }
        finally
        {
            _recordingBusy = false;
        }

        if (_closing || !ReferenceEquals(_recordingSession, session) || _recordingEnding)
        {
            return;
        }

        if (started)
        {
            _recordingResumePending = false;
            _recordingResumeFailures = 0;
            _log.Event("RECORD SESSION", "state=resumed", $"segment={session.SegmentCount + 1}", $"url={_channel.Url}");
            UpdateRecordingUi();
            return;
        }

        _recordingResumeFailures++;
        _log.Event("RECORD SESSION", "state=resume_failed", $"attempt={_recordingResumeFailures}", $"url={_channel.Url}");
        if (_recordingResumeFailures >= MaxResumeFailures)
        {
            await EndRecordingSessionAsync(RecordingEnd.ResumeFailed);
        }
    }

    /// <summary>
    /// Stats tick: keeps the badge's timer moving, notices an engine that stopped writing without saying so, and
    /// retries a resume the picture-live moment could not make.
    /// </summary>
    private void ObserveRecording()
    {
        if (_recordingSession is null || _recordingBusy || _recordingEnding)
        {
            return;
        }

        UpdateRecordTimer();
        if (_recordingResumePending)
        {
            ResumeRecordingIfPending();
            return;
        }

        if (!_backend.IsRecording)
        {
            HandlerBoundary.Run(nameof(ObserveRecording), CollectSilentlyEndedSegmentAsync);
        }
    }

    /// <summary>The engine is not writing although no segment was handed over: collect it and owe a resume.</summary>
    private async Task CollectSilentlyEndedSegmentAsync()
    {
        if (_recordingSession is not { } session)
        {
            return;
        }

        _recordingBusy = true;
        try
        {
            if (await _backend.StopRecordingAsync() is { } segment)
            {
                session.Add(segment);
            }
        }
        finally
        {
            _recordingBusy = false;
        }

        if (!_closing && ReferenceEquals(_recordingSession, session) && !_recordingEnding)
        {
            _recordingResumePending = true;
            _log.Event("RECORD SESSION", "state=engine_stopped_writing", $"url={_channel.Url}");
            UpdateRecordingUi();
        }
    }

    /// <summary>Playback failed for good; the recording ends with it, once the engine's stop has handed the segment over.</summary>
    private void EndRecordingAfterFailure(Task engineStopped)
    {
        if (_recordingSession is not null)
        {
            HandlerBoundary.Run(nameof(EndRecordingAfterFailure), () => EndRecordingSessionAsync(RecordingEnd.PlaybackFailed, engineStopped));
        }
    }

    /// <summary>
    /// Ends the recording: the last segment is handed over (by <paramref name="engineStopped"/> when the engine is
    /// stopping anyway, otherwise by asking it), every segment is finished, and one notice reports them all.
    /// </summary>
    private async Task EndRecordingSessionAsync(RecordingEnd why, Task? engineStopped = null)
    {
        if (_recordingSession is not { } session || _recordingEnding)
        {
            return;
        }

        if (_recordingBusy && engineStopped is null)
        {
            return; // a start or resume is waiting on the engine; the press after it lands is the one that counts
        }

        _recordingEnding = true;
        _recordingBusy = true;
        UpdateRecordingUi();
        if (!_closing && why != RecordingEnd.Smoke)
        {
            ShowFrameToast(LocalizationService.Get("RecordSaving"));
        }

        try
        {
            if (engineStopped is not null)
            {
                await engineStopped.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            else if (await _backend.StopRecordingAsync() is { } segment)
            {
                session.Add(segment);
            }

            var results = await session.CompleteAsync();
            LogRecordingSession(session, results, why);
            if (why == RecordingEnd.Smoke)
            {
                var saved = results.FirstOrDefault(result => result.Fate == SegmentFate.Saved);
                SmokeRecording.Report(_log, "video", saved is null ? results.FirstOrDefault()?.Fate.ToString().ToLowerInvariant() ?? "none" : "saved", saved?.Path);
            }

            if (!_closing)
            {
                ShowFrameToast(DescribeRecording(results, why), RecordingNoticeHoldMs);
            }
        }
        finally
        {
            _recordingSession = null;
            _recordingEnding = false;
            _recordingBusy = false;
            _recordingResumePending = false;
            if (!_closing)
            {
                UpdateRecordingUi();
            }
        }
    }

    /// <summary>
    /// The window is closing: teardown ends the segment and hands it over, so the session only has to wait for the
    /// engine and then finish. The finish task is what the application's close work awaits (SP-0164) - quitting
    /// gives it the close-work deadline, and a file the deadline outlives is left in staging for the next start
    /// to hand over (<see cref="RecordingStaging"/>). Nothing is shown - the window is gone - but every outcome
    /// is logged.
    /// </summary>
    internal Task? CloseRecordingFinish { get; private set; }

    private void FinishRecordingOnClose(Task engineReleased)
    {
        if (_recordingSession is not { } session)
        {
            return;
        }

        if (_recordingEnding)
        {
            // An end is already finishing this session (a user stop taken by the close); join it so the quit
            // path waits for its move too. The join only waits - the in-flight end does the reporting.
            CloseRecordingFinish = session.CompleteAsync();
            return;
        }

        _recordingEnding = true;
        CloseRecordingFinish = FinishAsync();

        async Task FinishAsync()
        {
            try
            {
                await engineReleased.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                var results = await session.CompleteAsync().ConfigureAwait(false);
                LogRecordingSession(session, results, RecordingEnd.WindowClosed);
            }
            catch (Exception exception)
            {
                _log.Event("RECORD SESSION", "state=close_failed", $"err={exception.Message}");
            }
        }
    }

    private void LogRecordingSession(VideoRecordingSession session, IReadOnlyList<SegmentResult> results, RecordingEnd why) =>
        _log.Event("RECORD SESSION",
            "state=ended",
            $"why={why.ToString().ToLowerInvariant()}",
            $"length={RecordingLength.Format(DateTimeOffset.Now - session.StartedAt)}",
            $"segments={results.Count}",
            $"saved={results.Count(result => result.Fate == SegmentFate.Saved)}",
            $"stranded={results.Count(result => result.Fate == SegmentFate.Stranded)}",
            $"empty={results.Count(result => result.Fate == SegmentFate.Empty)}",
            $"url={_channel.Url}");

    /// <summary>One sentence for the whole recording. A file that could not be moved outranks the rest: it is the one the user has to go and find.</summary>
    private static string DescribeRecording(IReadOnlyList<SegmentResult> results, RecordingEnd why)
    {
        var saved = results.Where(result => result.Fate == SegmentFate.Saved && result.Path is not null).ToList();
        var stranded = results.FirstOrDefault(result => result.Fate == SegmentFate.Stranded);
        // SP-0179: a segment the recording's folder refused by the time it was finished went to a fallback -
        // told next, because it is not where the user expects it (CAPTURE-OUTPUT rule 11).
        var elsewhere = saved.FirstOrDefault(result => result.Skipped is not null);
        var body = stranded is not null
            ? LocalizationService.Format("RecordStranded", stranded.Path)
            : elsewhere is not null
            ? LocalizationService.Format("RecordSavedElsewhere", elsewhere.Skipped, elsewhere.Path)
            : saved.Count switch
            {
                0 => LocalizationService.Get("RecordNothingSaved"),
                1 => LocalizationService.Format("RecordSaved", Path.GetFileName(saved[0].Path)),
                _ => LocalizationService.Format("RecordSavedParts", saved.Count, Path.GetDirectoryName(saved[0].Path))
            };
        return why is RecordingEnd.PlaybackFailed or RecordingEnd.ResumeFailed
            ? LocalizationService.Format("RecordEnded", body)
            : body;
    }

    private void UpdateRecordingUi()
    {
        var reason = _backend.RecordUnavailableReason;
        var active = _recordingSession is not null && !_recordingEnding;
        RecordButton.Style = (Style)FindResource(active ? "PlayerOverlayStopRecordGlyphButton" : "PlayerOverlayRecordGlyphButton");
        RecordButton.IsEnabled = reason is null && !_recordingEnding;
        ToolTipService.SetShowOnDisabled(RecordButton, true);
        // Resource references, not assigned strings: the name follows the role here and must also follow a
        // language change made while recording (APP-BEHAVIOUR rule 9, SP-0114).
        RecordButton.SetResourceReference(ToolTipProperty, reason ?? (active ? "StopRecordTip" : "RecordTip"));
        RecordButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, active ? "StopRecord" : "Record");
        RecordIndicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        // Dimmed between segments: the engine is not writing, and the badge must not say it is (R1).
        RecordIndicator.Opacity = _recordingResumePending ? 0.45 : 1.0;
        if (active)
        {
            UpdateRecordTimer();
        }
    }

    private void UpdateRecordTimer()
    {
        if (_recordingSession is { } session)
        {
            RecordTimerText.Text = $"REC {RecordingLength.Format(DateTimeOffset.Now - session.StartedAt)}";
        }
    }

    private bool _smokeRecordingActive;

    /// <summary>SP-0121 R8: the smoke gate's recording - a few seconds from the first live picture, then stop and report.</summary>
    private void StartSmokeRecordingIfAsked()
    {
        if (SmokeRecording.Duration is not { } duration || !SmokeRecording.TryClaim())
        {
            return;
        }

        _smokeRecordingActive = true;
        HandlerBoundary.Run(nameof(StartSmokeRecordingIfAsked), async () =>
        {
            if (_backend.RecordUnavailableReason is { } reason)
            {
                SmokeRecording.Report(_log, "video", $"unavailable:{reason}", null);
                return;
            }

            await StartRecordingSessionAsync();
            if (_recordingSession is null)
            {
                SmokeRecording.Report(_log, "video", "start_failed", null);
                return;
            }

            await Task.Delay(duration);
            await EndRecordingSessionAsync(RecordingEnd.Smoke);
        });
    }
}
