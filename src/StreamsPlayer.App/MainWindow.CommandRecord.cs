using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0273: the first instance's end of the recording command group - <c>record start</c> and <c>record stop</c>. Both go
/// through the recorders the window's own Record controls use (the radio's byte copy, the player window's recording session
/// and the segment finisher), so a command and a click cannot disagree about what a recording is. What is new is only what a
/// command needs that a click does not: the folder is the caller's, and there is a verdict.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes there or nowhere (APP-COMMANDS rule 9).</b> Each recorder is handed a chain of exactly one folder - the resolved
/// <c>--out</c> - so nothing falls back to the role default or Downloads; a refused folder is told as a full volume (13) or an
/// unwritable folder (14), after a probe that writes and flushes a block. A refused command creates no folder: the stream is
/// judged recordable first.
/// </para>
/// <para>
/// <b>Never a dialog (rule 4).</b> Every route here ends in a status line, a toast the window already shows, or an answer;
/// nothing waits on a person.
/// </para>
/// <para>
/// <b>An exit 2 never cancels.</b> A start that has seen no first data inside its bound, and a finish that outlasts its bound,
/// answer <c>timeout</c> and leave the recording as it is: a second <c>record start</c> answers the running recording and
/// <c>record stop</c> ends it.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>A margin under the second process's own bound, so the verdict that comes back is ours when it is ours to give.</summary>
    private static readonly TimeSpan CommandVerdictMargin = TimeSpan.FromSeconds(1);

    /// <summary><c>record start</c>: record the playing stream into the folder <c>--out</c> names, until the first data is seen.</summary>
    internal async Task<AppCommandResponse> RecordStartForCommandAsync(
        string requestId, AppCommandEntry entry, AppCommandParse parse, AppCommandRequest request, CancellationToken cancellationToken)
    {
        var (folder, violation) = AppRecordCommands.ResolveOut(parse.Value("out"), request.WorkingDir);
        if (folder is null)
        {
            return AppRecordCommands.OutRefusal(requestId, violation);
        }

        var plan = AppRecordCommands.PlanStart(requestId, ChannelIdOf(parse), CommandSessions());
        if (plan.Target is not { } target)
        {
            return plan.Refusal ?? AppRecordCommands.StartFailed(requestId);
        }

        var bound = RecordVerdictBound(entry);
        var response = target.Kind == AppPlaybackSession.AudioKind
            ? await StartAudioRecordingForCommandAsync(requestId, target, folder, bound, cancellationToken)
            : await StartVideoRecordingForCommandAsync(requestId, target, folder, bound, cancellationToken);
        _log.Event("COMMAND RECORD", "op=start", $"kind={target.Kind}", $"exit={response.ExitCode}");
        return response;
    }

    /// <summary><c>record stop</c>: finish the recording the request names, and report the file, its size and its length.</summary>
    internal async Task<AppCommandResponse> RecordStopForCommandAsync(
        string requestId, AppCommandEntry entry, AppCommandParse parse, CancellationToken cancellationToken)
    {
        var plan = AppRecordCommands.PlanStop(requestId, ChannelIdOf(parse), CommandSessions());
        if (plan.Target is not { } target)
        {
            return plan.Refusal ?? AppRecordCommands.TargetMissing(requestId, AppRecordMessages.NothingRecording);
        }

        var bound = RecordVerdictBound(entry);
        var response = target.Kind == AppPlaybackSession.AudioKind
            ? await StopAudioRecordingForCommandAsync(requestId, bound, cancellationToken)
            : await StopVideoRecordingForCommandAsync(requestId, target, bound, cancellationToken);
        _log.Event("COMMAND RECORD", "op=stop", $"kind={target.Kind}", $"exit={response.ExitCode}");
        return response;
    }

    // ---- radio ----------------------------------------------------------------------------------------------------------

    private async Task<AppCommandResponse> StartAudioRecordingForCommandAsync(
        string requestId, AppPlaybackSession target, string folder, TimeSpan bound, CancellationToken cancellationToken)
    {
        if (_audioRecorder is { } running)
        {
            return await JoinAudioRecordingAsync(requestId, running, folder, bound, cancellationToken);
        }

        if (AudioRecordUnavailableReason() is not null)
        {
            return AppRecordCommands.NotRecordable(requestId, AppRecordMessages.NotRecordableBroadcast);
        }

        var state = await Task.Run(() => CommandRecordingFolder.Probe(folder));
        if (state != RecordingFolderState.Ready)
        {
            return CommandRecordingFolder.Refusal(requestId, state);
        }

        // The probe was awaited: the radio may have been stopped or switched, or a person may have pressed Record, meanwhile.
        if (_audioRecorder is { } meanwhile)
        {
            return await JoinAudioRecordingAsync(requestId, meanwhile, folder, bound, cancellationToken);
        }

        if (_playingAudio is not { } playing || playing.Channel.Id != target.ChannelId)
        {
            return AppRecordCommands.TargetMissing(requestId, AppRecordMessages.StreamNotOpen);
        }

        StartAudioRecording([folder]);
        return _audioRecorder is { } recorder
            ? await AwaitAudioFirstBytesAsync(requestId, recorder, folder, already: false, bound, cancellationToken)
            : AppRecordCommands.StartFailed(requestId);
    }

    /// <summary>A recording already runs: the same folder is the same recording (idempotent), another folder is refused.</summary>
    private Task<AppCommandResponse> JoinAudioRecordingAsync(
        string requestId, StreamAudioRecorder running, string folder, TimeSpan bound, CancellationToken cancellationToken) =>
        AppRecordCommands.SameFolder(running.RequestedFolder, folder)
            ? AwaitAudioFirstBytesAsync(requestId, running, folder, already: true, bound, cancellationToken)
            : Task.FromResult(AppRecordCommands.OtherFolder(requestId));

    /// <summary>
    /// "Accepted" is the recorder's own connection delivering its first byte into the file: the answer carries that file. A
    /// recording that ends before a byte arrived is told by its outcome; one still silent at the bound is not proven (2).
    /// </summary>
    private async Task<AppCommandResponse> AwaitAudioFirstBytesAsync(
        string requestId, StreamAudioRecorder recorder, string folder, bool already, TimeSpan bound, CancellationToken cancellationToken)
    {
        if (!await CompletesWithinAsync(recorder.FirstBytes, bound, cancellationToken))
        {
            return AppRecordCommands.NotConfirmed(requestId, AppRecordMessages.StartNotConfirmed);
        }

        if (await recorder.FirstBytes is { } file)
        {
            return AppRecordCommands.Started(requestId, AppPlaybackSession.AudioKind, folder, file, already);
        }

        return AudioStartFailure(requestId, await recorder.Completion);
    }

    private static AppCommandResponse AudioStartFailure(string requestId, AudioRecordingOutcome outcome) => outcome.End switch
    {
        AudioRecordingEnd.WriteFailed => AppRecordCommands.WriteFailed(requestId,
            outcome.NoSpace ? AppRecordWriteProblem.NoSpace : AppRecordWriteProblem.NotWritable),
        AudioRecordingEnd.UnsupportedFormat => AppRecordCommands.NotRecordable(requestId, AppRecordMessages.NotRecordableFormat),
        AudioRecordingEnd.NotConnected => AppRecordCommands.StationNotAnswering(requestId),
        _ => AppRecordCommands.StartFailed(requestId)
    };

    private async Task<AppCommandResponse> StopAudioRecordingForCommandAsync(
        string requestId, TimeSpan bound, CancellationToken cancellationToken)
    {
        if (DetachAudioRecorder() is not { } recorder)
        {
            return AppRecordCommands.TargetMissing(requestId, AppRecordMessages.NothingRecording);
        }

        // The same stop and the same status line as the button; the file is written in place, so it is whole once the copy ends.
        var stopping = FinishAudioRecordingAsync(recorder);
        ObserveFault(stopping, "Recording stop failed");
        if (!await CompletesWithinAsync(stopping, bound, cancellationToken))
        {
            return AppRecordCommands.NotConfirmed(requestId, AppRecordMessages.FinishNotConfirmed);
        }

        return AudioStopResponse(requestId, await stopping);
    }

    private static AppCommandResponse AudioStopResponse(string requestId, AudioRecordingOutcome outcome)
    {
        var saved = outcome.Path is { } path && outcome.Bytes > 0
            ? new[] { new AppRecordedFile(path, outcome.Bytes, outcome.Length) }
            : null;
        if (outcome.End == AudioRecordingEnd.WriteFailed)
        {
            return AppRecordCommands.WriteFailed(requestId,
                outcome.NoSpace ? AppRecordWriteProblem.NoSpace : AppRecordWriteProblem.NotWritable, saved);
        }

        return saved is null ? AppRecordCommands.NothingSaved(requestId) : AppRecordCommands.Finished(requestId, saved);
    }

    // ---- video ----------------------------------------------------------------------------------------------------------

    private async Task<AppCommandResponse> StartVideoRecordingForCommandAsync(
        string requestId, AppPlaybackSession target, string folder, TimeSpan bound, CancellationToken cancellationToken)
    {
        var window = _playerWindows.FirstOrDefault(candidate => !candidate.IsClosingForCommand && candidate.Channel.Id == target.ChannelId);
        if (window is null)
        {
            return AppRecordCommands.TargetMissing(requestId, AppRecordMessages.StreamNotOpen);
        }

        if (window.IsRecordingEndingForCommand)
        {
            return AppRecordCommands.RecordingBusy(requestId);
        }

        if (window.RecordingFolderForCommand is { } running)
        {
            if (!AppRecordCommands.SameFolder(running, folder))
            {
                return AppRecordCommands.OtherFolder(requestId);
            }

            // Idempotent, but never a claim the engine does not bear out: a dimmed badge is not a recording.
            return window.IsRecordingWritingForCommand
                ? AppRecordCommands.Started(requestId, AppPlaybackSession.VideoKind, folder, file: null, already: true)
                : AppRecordCommands.NotConfirmed(requestId, AppRecordMessages.StartNotConfirmed);
        }

        var state = await Task.Run(() => CommandRecordingFolder.Probe(folder));
        if (state != RecordingFolderState.Ready)
        {
            return CommandRecordingFolder.Refusal(requestId, state);
        }

        var started = await window.StartRecordingForCommandAsync(folder, bound, cancellationToken);
        return started switch
        {
            PlayerRecordingStart.Started => AppRecordCommands.Started(requestId, AppPlaybackSession.VideoKind, folder, file: null, already: false),
            PlayerRecordingStart.NotLive => AppRecordCommands.TargetMissing(requestId, AppRecordMessages.NotLiveYet),
            PlayerRecordingStart.Busy => AppRecordCommands.RecordingBusy(requestId),
            PlayerRecordingStart.Unavailable => AppRecordCommands.NotRecordable(requestId, AppRecordMessages.NotRecordableEngine),
            PlayerRecordingStart.NoWritableFolder => AppRecordCommands.WriteFailed(requestId, AppRecordWriteProblem.NotWritable),
            PlayerRecordingStart.NotConfirmed => AppRecordCommands.NotConfirmed(requestId, AppRecordMessages.StartNotConfirmed),
            _ => AppRecordCommands.StartFailed(requestId)
        };
    }

    private async Task<AppCommandResponse> StopVideoRecordingForCommandAsync(
        string requestId, AppPlaybackSession target, TimeSpan bound, CancellationToken cancellationToken)
    {
        var window = _playerWindows.FirstOrDefault(candidate =>
            !candidate.IsClosingForCommand && candidate.Channel.Id == target.ChannelId && candidate.IsRecordingForCommand);
        if (window is null)
        {
            return AppRecordCommands.TargetMissing(requestId, AppRecordMessages.NothingRecording);
        }

        if (window.IsRecordingEndingForCommand)
        {
            return AppRecordCommands.RecordingBusy(requestId);
        }

        // The window's own end: the engine's last segment is taken, every segment is finished through the segment finisher
        // (moved whole or not at all) and the window shows its usual notice.
        var ending = window.StopRecordingForCommandAsync();
        ObserveFault(ending, "Recording stop failed");
        if (!await CompletesWithinAsync(ending, bound, cancellationToken))
        {
            return AppRecordCommands.NotConfirmed(requestId, AppRecordMessages.FinishNotConfirmed);
        }

        var results = await ending;
        if (results is null)
        {
            // Nothing was ended: a start or resume was waiting on the engine, or the recording is gone.
            return window.IsRecordingForCommand
                ? AppRecordCommands.RecordingBusy(requestId)
                : AppRecordCommands.TargetMissing(requestId, AppRecordMessages.NothingRecording);
        }

        return await VideoStopResponseAsync(requestId, results);
    }

    /// <summary>
    /// What a finished video recording is: every saved segment (its size read off the disk, off the UI thread), or the segment
    /// the folder refused, which stays in staging for the next start.
    /// </summary>
    private static async Task<AppCommandResponse> VideoStopResponseAsync(string requestId, IReadOnlyList<SegmentResult> results)
    {
        var saved = await Task.Run(() => results
            .Where(result => result.Fate == SegmentFate.Saved && result.Path is not null)
            .Select(result => new AppRecordedFile(result.Path!, FileLength(result.Path!), result.Length))
            .ToList());
        if (results.FirstOrDefault(result => result.Fate == SegmentFate.Stranded) is { } stranded)
        {
            return AppRecordCommands.Stranded(requestId, stranded.NoSpace, saved);
        }

        return saved.Count > 0 ? AppRecordCommands.Finished(requestId, saved) : AppRecordCommands.NothingSaved(requestId);
    }

    // ---- shared ---------------------------------------------------------------------------------------------------------

    private static Guid? ChannelIdOf(AppCommandParse parse) =>
        parse.Value("id") is { } text && Guid.TryParse(text, out var parsed) ? parsed : null;

    private static TimeSpan RecordVerdictBound(AppCommandEntry entry)
    {
        var full = AppCommandPolicy.CompletionBound(entry);
        return full > CommandVerdictMargin ? full - CommandVerdictMargin : full;
    }

    /// <summary>True when <paramref name="task"/> completed inside <paramref name="bound"/>; false when the bound passed first.</summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan bound, CancellationToken cancellationToken)
    {
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = await Task.WhenAny(task, Task.Delay(bound, delay.Token));
        delay.Cancel();
        if (first == task)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// Gives a task nobody may be waiting on any more (the command answered 2, or its caller left) an observer, so a fault
    /// is logged and not lost. No dialog: a command is headless.
    /// </summary>
    private void ObserveFault(Task task, string what) =>
        _ = task.ContinueWith(finished => _log.Error(what, finished.Exception!), TaskContinuationOptions.OnlyOnFaulted);

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
