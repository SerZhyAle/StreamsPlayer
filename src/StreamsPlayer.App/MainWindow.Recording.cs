using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0101 / SP-0121: recording the radio station that is playing, and handing over what an earlier session left
/// in staging. The recorder runs on its own connection; a recording that ends on its own is reported the moment it
/// does, with the part that was saved and its length (R4), and a FastMediaSorter broadcast is not offered for
/// recording at all because its route allows exactly one listener connection (R6).
/// </summary>
public partial class MainWindow
{
    /// <summary>How long quitting waits for the recorder to close its file.</summary>
    private static readonly TimeSpan RecorderShutdownWait = TimeSpan.FromSeconds(2);

    private bool _smokeAudioRecording;

    // SP-0179: the first folder of the running recording's chain - named when nothing in the chain was writable.
    private string _audioRecordingFolder = string.Empty;

    /// <summary>The localization key saying why the playing station cannot be recorded, or null when it can.</summary>
    private string? AudioRecordUnavailableReason() =>
        _playingAudio is { } playing && UsesFastMediaSorterAudioRoute(playing.Channel)
            ? "RecordUnavailableFastMediaSorter"
            : null;

    private void ToggleAudioRecording()
    {
        if (_audioRecorder is null)
        {
            StartAudioRecording();
        }
        else
        {
            StopAudioRecording();
        }
    }

    private void StartAudioRecording()
    {
        if (_playingAudio is not { } playing)
        {
            return;
        }

        if (AudioRecordUnavailableReason() is { } reason)
        {
            SetStatus(reason);
            return;
        }

        // SP-0179: the smoke gate's folder is a chain of one - its check reads the file from exactly there.
        IReadOnlyList<string> chain = _smokeAudioRecording && SmokeRecording.Folder is { } smokeFolder
            ? new[] { smokeFolder }
            : CaptureFolders.Chain(CaptureKind.StreamAudio, CaptureFolderChoices.For(_state, CaptureKind.StreamAudio));
        _audioRecordingFolder = chain[0];
        var recorder = StreamAudioRecorder.Start(playing.Channel, chain, _log, AudioRecorder_Redirected);
        // Before subscribing: an end delivered at the subscribe point must still find the session (SP-0164).
        _audioRecorder = recorder;
        recorder.Ended += AudioRecorder_Ended;
        ApplyAudioTransportState();
        SetStatus("Recording");
    }

    /// <summary>
    /// Stops the recording the user (or a stop of the station) ended. The recorder is let go at once, so the button
    /// is free again immediately; the outcome is reported when the file is closed, off the UI thread.
    /// </summary>
    private void StopAudioRecording()
    {
        if (_audioRecorder is not { } recorder)
        {
            return;
        }

        _audioRecorder = null;
        recorder.Ended -= AudioRecorder_Ended;
        ApplyAudioTransportState();
        HandlerBoundary.Run(nameof(StopAudioRecording), async () =>
        {
            var outcome = await recorder.StopAsync();
            ReportAudioRecording(outcome);
        });
    }

    /// <summary>
    /// The chosen or default folder refused the file and it is being written further down the chain. Worker thread.
    /// CAPTURE-OUTPUT rule 11: told the moment it happens, naming both folders.
    /// </summary>
    private void AudioRecorder_Redirected(string skipped, string folder) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!_shuttingDown)
            {
                SetStatus("RecordFellBack", skipped, folder);
            }
        });

    /// <summary>The recording ended on its own - the station dropped it, or never sent audio. Worker thread.</summary>
    private void AudioRecorder_Ended(StreamAudioRecorder recorder, AudioRecordingOutcome outcome) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_audioRecorder, recorder))
            {
                return; // already stopped by the user; that path reports
            }

            _audioRecorder = null;
            recorder.Ended -= AudioRecorder_Ended;
            ApplyAudioTransportState();
            ReportAudioRecording(outcome);
        });

    private void ReportAudioRecording(AudioRecordingOutcome outcome)
    {
        if (_smokeAudioRecording)
        {
            _smokeAudioRecording = false;
            SmokeRecording.Report(_log, "audio", outcome.End.ToString().ToLowerInvariant(), outcome.Path);
        }

        if (_shuttingDown)
        {
            return;
        }

        var length = RecordingLength.Format(outcome.Length);
        var file = outcome.Path is null ? null : Path.GetFileName(outcome.Path);
        switch (outcome.End)
        {
            case AudioRecordingEnd.Stopped when file is not null:
                SetStatus("RecordSavedWithLength", file, length);
                break;
            case AudioRecordingEnd.Stopped:
                SetStatus("RecordNothingSaved");
                break;
            case AudioRecordingEnd.ConnectionLost when file is not null:
                SetStatus("RecordEndedConnectionLost", file, length);
                break;
            case AudioRecordingEnd.UnsupportedFormat:
                SetStatus("RecordUnsupportedFormat");
                break;
            case AudioRecordingEnd.WriteFailed:
                SetStatus("RecordWriteFailed", outcome.Path ?? _audioRecordingFolder);
                break;
            default:
                SetStatus("RecordStartFailed");
                break;
        }
    }

    /// <summary>Quitting: close the file within a bound, so the part recorded so far is complete on disk.</summary>
    private async Task StopAudioRecordingForShutdownAsync()
    {
        if (_audioRecorder is not { } recorder)
        {
            return;
        }

        _audioRecorder = null;
        recorder.Ended -= AudioRecorder_Ended;
        await Task.WhenAny(recorder.StopAsync(), Task.Delay(RecorderShutdownWait));
    }

    /// <summary>SP-0121 R8: the smoke gate's radio recording - a few seconds from the first live audio, then stop.</summary>
    private void StartAudioSmokeRecordingIfAsked()
    {
        if (SmokeRecording.Duration is not { } duration || !SmokeRecording.TryClaim())
        {
            return;
        }

        _smokeAudioRecording = true;
        StartAudioRecording();
        if (_audioRecorder is not { } recorder)
        {
            _smokeAudioRecording = false;
            SmokeRecording.Report(_log, "audio", "start_failed", null);
            return;
        }

        HandlerBoundary.Run(nameof(StartAudioSmokeRecordingIfAsked), async () =>
        {
            await Task.Delay(duration);
            if (ReferenceEquals(_audioRecorder, recorder))
            {
                StopAudioRecording();
            }
        });
    }

    /// <summary>
    /// R2: files a crash or a failed move left in staging are given to the user at the next start - moved into the
    /// recordings folder and announced, or, when that fails, announced with the place they are in.
    /// </summary>
    private async Task HandOverStagedRecordingsAsync()
    {
        // SP-0179: only the LibVLC video engine stages, so a leftover takes the video recordings' chain.
        var handOver = await RecordingStaging.HandOverLeftoversAsync(
            CaptureFolders.Chain(CaptureKind.StreamVideo, CaptureFolderChoices.For(_state, CaptureKind.StreamVideo)), _log);
        if (handOver.Stranded > 0)
        {
            SetStatus("RecordingsRecoveryFailed", handOver.Stranded, handOver.StagingRoot);
        }
        else if (handOver.Moved > 0)
        {
            SetStatus("RecordingsRecovered", handOver.Moved, handOver.TargetFolder);
        }
    }
}
