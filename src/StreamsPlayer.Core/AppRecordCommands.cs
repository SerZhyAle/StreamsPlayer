using System.Globalization;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>SP-0273: one finished recording file as <c>record stop</c> reports it.</summary>
public sealed record AppRecordedFile(string Path, long Bytes, TimeSpan Length);

/// <summary>
/// SP-0273: what a failed write was, for the exit it earns (<c>APP-COMMANDS</c> rule 11: 13 resources, 14 I/O).
/// </summary>
public enum AppRecordWriteProblem
{
    /// <summary>The volume is full: exit 13, reason <c>no-space</c>.</summary>
    NoSpace,

    /// <summary>The folder or the file cannot be written for any other reason: exit 14, reason <c>io-error</c>.</summary>
    NotWritable
}

/// <summary>The stream a record verb acts on, or the refusal when the request does not name one.</summary>
public sealed record AppRecordPlan(AppPlaybackSession? Target, AppCommandResponse? Refusal);

/// <summary>
/// SP-0273: the sentences of the recording verbs. English, one sentence each, never an address, a path or a title - the
/// same rule <see cref="AppCommandMessages"/> keeps.
/// </summary>
public static class AppRecordMessages
{
    public const string StartOptionsOnce = "The command \"record start\" takes --out once and --id at most once.";

    public const string StopOptionsOnce = "The command \"record stop\" takes --id at most once.";

    public const string OutNotUsable = "The value of --out is not a usable folder path.";

    public const string OutNeedsFullPath =
        "A relative --out needs the caller's working folder, which this request did not carry; give a full path.";

    public const string OutIsFile = "The path of --out is a file, not a folder.";

    /// <summary>SP-0279, CAPTURE-OUTPUT 0.8: a folder the caller names is never created.</summary>
    public const string OutMissing = "The folder of --out does not exist; this command never creates it.";

    public const string NothingOpen = "Nothing is playing, so there is nothing to record; start a stream with play first.";

    public const string StartChoose = "Several streams are open; add --id <channel id> to say which one to record.";

    public const string StreamNotOpen = "No open stream has this id.";

    public const string RadioPaused = "The radio is paused; resume it with playback resume before recording.";

    public const string StreamFailedOpen = "This stream failed to open, so there is nothing to record.";

    public const string NotLiveYet = "The stream is not playing yet; record again once the picture is up.";

    public const string AlreadyElsewhere =
        "This stream is already being recorded into another folder; run record stop first.";

    public const string NotRecordableBroadcast = "A StreamsPlayer broadcast allows one listener, so it cannot be recorded.";

    public const string NotRecordableFormat = "This station's format cannot be recorded as a copy of its bytes.";

    public const string NotRecordableEngine = "The video engine in use cannot record this stream.";

    public const string NoRoom = "There is not enough space for the recording.";

    public const string NotWritable = "The folder of --out cannot be written.";

    public const string StartFailed = "The recording could not be started on this stream; the log has the details.";

    public const string StationNotAnswering = "The station did not answer the recording's own connection.";

    public const string AlreadyRecording = "This stream is already being recorded.";

    public const string StartNotConfirmed =
        "No recorded data was seen in time; the recording may still be starting - run record start again, or record stop.";

    public const string NothingRecording = "Nothing is being recorded.";

    public const string StopChoose = "Several streams are being recorded; add --id <channel id> to say which one to stop.";

    public const string NotRecording = "This stream is not being recorded.";

    public const string RecordingBusy = "A recording of this stream is being started or finished; try again in a moment.";

    public const string NothingSaved = "The recording ended with nothing saved.";

    public const string Stranded =
        "The recording could not be moved into the folder of --out; it is kept for the next start and the log has the details.";

    public const string FinishNotConfirmed =
        "The recording has not finished saving within the bound; the file may still be completing.";

    public static string Started(string kind) => $"Recording the {kind} stream.";

    public static string Finished(int parts) =>
        parts <= 1 ? "The recording is saved." : string.Create(CultureInfo.InvariantCulture, $"The recording is saved in {parts} parts.");
}

/// <summary>
/// SP-0273: the recording command group's decisions that need no window - which options go together, where <c>--out</c> is,
/// which stream a verb acts on, what a failed write is, and every answer. The App executes; this decides, so the rules are
/// tested without a player or a disk.
/// </summary>
public static class AppRecordCommands
{
    public const string StartVerb = "record start";
    public const string StopVerb = "record stop";

    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    /// <summary>
    /// The rules of the two verbs that the manifest alone cannot express (rule 3). <see langword="null"/> when the options
    /// are acceptable. Run by the one parser, so the second process and the first instance judge them alike.
    /// </summary>
    public static AppPlaybackCommands.Violation? Check(AppCommandEntry entry, IReadOnlyDictionary<string, IReadOnlyList<string>> options) =>
        entry.Name switch
        {
            StartVerb => CheckStart(options),
            StopVerb => CheckStop(options),
            _ => null
        };

    /// <summary>
    /// The full path <c>--out</c> names. A relative path is resolved against the caller's working folder (rule 7), never
    /// against the running app's own; with no usable working folder it is refused. A trailing separator is dropped.
    /// </summary>
    public static (string? Folder, AppPlaybackCommands.Violation? Violation) ResolveOut(string? value, string? workingDirectory)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || !IsUsablePathText(text))
        {
            return (null, Bad(AppRecordMessages.OutNotUsable));
        }

        try
        {
            string? full;
            if (Path.IsPathFullyQualified(text))
            {
                full = Path.GetFullPath(text);
            }
            else if (!string.IsNullOrWhiteSpace(workingDirectory) && Path.IsPathFullyQualified(workingDirectory))
            {
                full = Path.GetFullPath(text, workingDirectory);
            }
            else
            {
                full = null;
            }

            if (full is null)
            {
                return (null, Bad(AppRecordMessages.OutNeedsFullPath));
            }

            return (Path.TrimEndingDirectorySeparator(full), null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, Bad(AppRecordMessages.OutNotUsable));
        }
    }

    /// <summary>Whether two folders are the same one - full paths, trailing separator and case ignored.</summary>
    public static bool SameFolder(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The stream <c>record start</c> acts on (rule 4: more than one and no choice is refused, never guessed). A paused radio and
    /// a window that failed to open are not streams that can be recorded.
    /// </summary>
    public static AppRecordPlan PlanStart(string requestId, Guid? channelId, IReadOnlyList<AppPlaybackSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        AppPlaybackSession? target;
        if (channelId is { } id)
        {
            target = sessions.FirstOrDefault(session => session.ChannelId == id);
            if (target is null)
            {
                return Missing(requestId, AppRecordMessages.StreamNotOpen);
            }
        }
        else if (sessions.Count == 0)
        {
            return Missing(requestId, AppRecordMessages.NothingOpen);
        }
        else if (sessions.Count > 1)
        {
            return new AppRecordPlan(null, AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                AppRecordMessages.StartChoose));
        }
        else
        {
            target = sessions[0];
        }

        return target.State switch
        {
            AppPlaybackState.Paused => Missing(requestId, AppRecordMessages.RadioPaused),
            AppPlaybackState.Failed => Missing(requestId, AppRecordMessages.StreamFailedOpen),
            _ => new AppRecordPlan(target, null)
        };
    }

    /// <summary>The recording <c>record stop</c> ends: the named stream when it is being recorded, else the only one that is.</summary>
    public static AppRecordPlan PlanStop(string requestId, Guid? channelId, IReadOnlyList<AppPlaybackSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (channelId is { } id)
        {
            var named = sessions.FirstOrDefault(session => session.ChannelId == id);
            if (named is null)
            {
                return Missing(requestId, AppRecordMessages.StreamNotOpen);
            }

            return named.Recording ? new AppRecordPlan(named, null) : Missing(requestId, AppRecordMessages.NotRecording);
        }

        var recording = sessions.Where(session => session.Recording).ToArray();
        return recording.Length switch
        {
            0 => Missing(requestId, AppRecordMessages.NothingRecording),
            1 => new AppRecordPlan(recording[0], null),
            _ => new AppRecordPlan(null, AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                AppRecordMessages.StopChoose))
        };
    }

    /// <summary>
    /// What a refused write is: a full volume (an <see cref="IOException"/> whose HRESULT is the Win32 disk-full or handle-disk-full
    /// error) is <see cref="AppRecordWriteProblem.NoSpace"/>; every other refusal of a folder or a file is not writable.
    /// </summary>
    public static AppRecordWriteProblem ClassifyWrite(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is IOException && exception.HResult is DiskFull or HandleDiskFull
            ? AppRecordWriteProblem.NoSpace
            : AppRecordWriteProblem.NotWritable;
    }

    // ---- the answers --------------------------------------------------------------------------------------------------

    /// <summary>
    /// A recording is running. <paramref name="folder"/> is the one the caller named; <paramref name="file"/> is known for a radio
    /// recording only (a video file is named when it ends).
    /// </summary>
    public static AppCommandResponse Started(string requestId, string kind, string folder, string? file, bool already)
    {
        var data = new JsonObject { ["folder"] = folder, ["kind"] = kind };
        if (file is not null)
        {
            data["file"] = file;
        }

        return AppCommandResponse.Done(requestId, already ? AppRecordMessages.AlreadyRecording : AppRecordMessages.Started(kind), data);
    }

    /// <summary>The recording is finished: the first part as <c>file</c>, the totals, and how many parts there are.</summary>
    public static AppCommandResponse Finished(string requestId, IReadOnlyList<AppRecordedFile> files) =>
        AppCommandResponse.Done(requestId, AppRecordMessages.Finished(files.Count), FinishedData(files));

    /// <summary>The <c>data</c> of <c>record stop</c>; <paramref name="files"/> must not be empty.</summary>
    public static JsonObject FinishedData(IReadOnlyList<AppRecordedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("A finished recording has at least one file.", nameof(files));
        }

        return new JsonObject
        {
            ["file"] = files[0].Path,
            ["bytes"] = files.Sum(file => file.Bytes),
            ["seconds"] = (int)Math.Round(files.Sum(file => file.Length.TotalSeconds), MidpointRounding.AwayFromZero),
            ["parts"] = files.Count
        };
    }

    /// <summary>A write that failed: exit 13 for a full volume, 14 otherwise; <paramref name="saved"/> is what was written before it.</summary>
    public static AppCommandResponse WriteFailed(
        string requestId, AppRecordWriteProblem problem, IReadOnlyList<AppRecordedFile>? saved = null) =>
        WithSaved(problem == AppRecordWriteProblem.NoSpace
            ? AppCommandResponse.Failed(requestId, AppCommandReason.NoSpace, AppCommandExit.Resources, AppRecordMessages.NoRoom)
            : AppCommandResponse.Failed(requestId, AppCommandReason.IoError, AppCommandExit.Io, AppRecordMessages.NotWritable), saved);

    /// <summary>A finished segment the folder refused: it stays in staging for the next start; what was saved is still reported.</summary>
    public static AppCommandResponse Stranded(string requestId, bool noSpace, IReadOnlyList<AppRecordedFile>? saved = null) =>
        WithSaved(noSpace
            ? AppCommandResponse.Failed(requestId, AppCommandReason.NoSpace, AppCommandExit.Resources, AppRecordMessages.Stranded)
            : AppCommandResponse.Failed(requestId, AppCommandReason.IoError, AppCommandExit.Io, AppRecordMessages.Stranded), saved);

    /// <summary>The refusal of an <c>--out</c> that <see cref="ResolveOut"/> could not resolve (exit 10).</summary>
    public static AppCommandResponse OutRefusal(string requestId, AppPlaybackCommands.Violation? violation) =>
        violation is { } refused
            ? AppCommandResponse.Refused(requestId, refused.Reason, AppCommandExit.Usage, refused.Message)
            : AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage, AppRecordMessages.OutNotUsable);

    /// <summary>The path of <c>--out</c> exists and is a file.</summary>
    public static AppCommandResponse OutIsFile(string requestId) =>
        AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage, AppRecordMessages.OutIsFile);

    /// <summary>
    /// SP-0279, CAPTURE-OUTPUT 0.8: the folder of <c>--out</c> does not exist. A folder the caller names is never created, so
    /// the thing the verb names is missing: <c>failed</c>, <c>target-missing</c>, exit 11 (rule 12).
    /// </summary>
    public static AppCommandResponse OutMissing(string requestId) => TargetMissing(requestId, AppRecordMessages.OutMissing);

    /// <summary>The stream cannot be recorded by design; <paramref name="message"/> says why.</summary>
    public static AppCommandResponse NotRecordable(string requestId, string message) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.NotRecordable, AppCommandExit.Failed, message);

    /// <summary>The recorder accepted the start and could not go on, with no more exact cause.</summary>
    public static AppCommandResponse StartFailed(string requestId) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.Error, AppCommandExit.Failed, AppRecordMessages.StartFailed);

    /// <summary>The station refused the recording's own connection.</summary>
    public static AppCommandResponse StationNotAnswering(string requestId) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.StreamFailed, AppCommandExit.Failed, AppRecordMessages.StationNotAnswering);

    /// <summary>The recording ended with no file.</summary>
    public static AppCommandResponse NothingSaved(string requestId) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.Error, AppCommandExit.Failed, AppRecordMessages.NothingSaved);

    /// <summary>The verdict did not come inside the bound (rule 8); nothing was cancelled, so a retry is safe.</summary>
    public static AppCommandResponse NotConfirmed(string requestId, string message) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.Timeout, AppCommandExit.NotProven, message);

    /// <summary>A recording of this stream runs into another folder.</summary>
    public static AppCommandResponse OtherFolder(string requestId) =>
        AppCommandResponse.Busy(requestId, AppRecordMessages.AlreadyElsewhere);

    /// <summary>The recording is being started or finished already (a person pressed a button, or a command did).</summary>
    public static AppCommandResponse RecordingBusy(string requestId) =>
        AppCommandResponse.Busy(requestId, AppRecordMessages.RecordingBusy);

    /// <summary>Nothing to act on: the target does not exist or is not ready (exit 11).</summary>
    public static AppCommandResponse TargetMissing(string requestId, string message) =>
        AppCommandResponse.Failed(requestId, AppCommandReason.TargetMissing, AppCommandExit.NotReady, message);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static AppCommandResponse WithSaved(AppCommandResponse response, IReadOnlyList<AppRecordedFile>? saved) =>
        saved is { Count: > 0 } ? response with { Data = FinishedData(saved) } : response;

    private static AppRecordPlan Missing(string requestId, string message) => new(null, TargetMissing(requestId, message));

    private static AppPlaybackCommands.Violation? CheckStart(IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        if (Count(options, "out") != 1 || Count(options, "id") > 1)
        {
            return Bad(AppRecordMessages.StartOptionsOnce);
        }

        return IsUsablePathText(AppPlaybackCommands.Last(options, "out")!) ? null : Bad(AppRecordMessages.OutNotUsable);
    }

    private static AppPlaybackCommands.Violation? CheckStop(IReadOnlyDictionary<string, IReadOnlyList<string>> options) =>
        Count(options, "id") > 1 ? Bad(AppRecordMessages.StopOptionsOnce) : null;

    private static bool IsUsablePathText(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static int Count(IReadOnlyDictionary<string, IReadOnlyList<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values.Count : 0;

    private static AppPlaybackCommands.Violation Bad(string message) => new(AppCommandReason.BadArgument, message);
}
