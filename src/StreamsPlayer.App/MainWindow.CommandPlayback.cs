using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Nodes;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0272: the first instance's end of the playback command group - <c>play</c>, <c>playback stop|pause|resume|status</c>
/// and <c>volume set</c>. Every verb goes through the code the window's own controls use (the play route, the stop
/// funnel, the pause and resume of the transport button, the volume slider's save), so a command and a click cannot
/// disagree about what the player does. What is new is only what a command needs that a click does not: a verdict.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never a dialog it waits on (rule 4).</b> A play runs the ordinary route with <c>quiet</c> set - the flag a start-up
/// resume already uses, which turns every modal of that route into a status line - and refuses beforehand what would
/// still need a person: a broadcast whose link password is not stored (<c>needs-person</c>, 12), and an unreadable or
/// invalid <c>.spres</c> (10). The launch paths that can open a window, found in the code for this ticket: the broadcast
/// trust prompt (<c>ResolveBroadcastTrustAsync</c>, skipped when quiet), the password prompt
/// (<c>ResolveBroadcastCredentials</c>, skipped when quiet), the not-launchable, offline, broadcast-unavailable and
/// failure dialogs of <c>PlayChannelAsync</c> (status lines when quiet), the busy, unreadable and invalid message boxes
/// of the <c>.spres</c> import (answered before the import runs), and the failure dialog of a player window (held back
/// until live when quiet).
/// </para>
/// <para>
/// <b>Done is the first sound or picture.</b> The command registers one watch for the channel it starts; the audio
/// proof (<c>AUDIO HEARD</c>), the broadcast route's live event, the terminal-failure funnel and the player window's own
/// first picture or terminal failure settle it. Commands run one at a time, so there is at most one watch.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private sealed class CommandPlaybackWatch(Guid channelId)
    {
        internal Guid ChannelId { get; } = channelId;

        internal TaskCompletionSource<PlaybackSettlement> Settled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private CommandPlaybackWatch? _commandWatch;

    /// <summary>True from the first audible output of a leg until it ends; what <c>playback status</c> calls playing.</summary>
    private bool _audioHeard;

    private void NoteAudioHeard()
    {
        if (_playingAudio is not { } playing)
        {
            return;
        }

        _audioHeard = true;
        SettleCommandWatch(playing.Channel.Id, new PlaybackSettlement(true, null, PlaybackReachability.NotProbed));
    }

    private void OnPlayerSettled(PlayerWindow window, PlaybackSettlement settlement) =>
        SettleCommandWatch(window.Channel.Id, settlement);

    private void SettleCommandWatch(Guid channelId, PlaybackSettlement settlement)
    {
        if (_commandWatch is { } watch && watch.ChannelId == channelId)
        {
            watch.Settled.TrySetResult(settlement);
        }
    }

    /// <summary>Every stream that is open now: the one audio session (playing or paused) and each player window.</summary>
    private IReadOnlyList<AppPlaybackSession> CommandSessions()
    {
        var sessions = new List<AppPlaybackSession>();
        if (_playingAudio is { } playing)
        {
            sessions.Add(new AppPlaybackSession(
                AppPlaybackSession.AudioKind, playing.Channel.Id, StreamTitleFormatter.Display(playing.Channel.Title),
                _audioHeard ? AppPlaybackState.Playing : AppPlaybackState.Buffering, _audioRecorder is not null));
        }
        else if (_audioPausedChannelId is { } pausedId)
        {
            sessions.Add(new AppPlaybackSession(
                AppPlaybackSession.AudioKind, pausedId, StreamTitleFormatter.Display(PausedAudioChannel?.Title ?? string.Empty),
                AppPlaybackState.Paused, Recording: false));
        }

        foreach (var window in _playerWindows.Where(candidate => !candidate.IsClosingForCommand))
        {
            sessions.Add(new AppPlaybackSession(
                AppPlaybackSession.VideoKind, window.Channel.Id, StreamTitleFormatter.Display(window.Channel.Title),
                window.CommandState, window.IsRecordingForCommand));
        }

        return sessions;
    }

    /// <summary>The open stream that already plays <paramref name="channel"/> - by id, or by its address - if there is one.</summary>
    private (AppPlaybackSession Session, PlayerWindow? Window)? FindPlaying(StreamChannel channel)
    {
        var address = CatalogUrlIdentity.Normalize(channel.Url);
        bool Same(StreamChannel other) =>
            other.Id == channel.Id || string.Equals(CatalogUrlIdentity.Normalize(other.Url), address, StringComparison.Ordinal);

        if (_playingAudio is { } playing && Same(playing.Channel))
        {
            return (CommandSessions().First(session => session.Kind == AppPlaybackSession.AudioKind), null);
        }

        foreach (var window in _playerWindows.Where(candidate => !candidate.IsClosingForCommand && candidate.CommandState != AppPlaybackState.Failed))
        {
            if (Same(window.Channel))
            {
                return (new AppPlaybackSession(AppPlaybackSession.VideoKind, window.Channel.Id,
                    StreamTitleFormatter.Display(window.Channel.Title), window.CommandState, window.IsRecordingForCommand), window);
            }
        }

        return null;
    }

    private bool HasOpenStream(Guid channelId) =>
        _playingAudio?.Channel.Id == channelId || _playerWindows.Any(candidate => candidate.Channel.Id == channelId);

    /// <summary><c>play</c>: the channel, the address or the broadcast file it names, until the first sound or picture.</summary>
    internal async Task<AppCommandResponse> PlayForCommandAsync(
        string requestId, AppCommandParse parse, AppCommandRequest request, CancellationToken cancellationToken)
    {
        // SP-0291 (S4-2): the guard channel add uses. The window is loaded before its catalog and saved preferences are
        // in memory, so a play that slips in there answers for a target that cannot be missing and takes the default
        // volume for the saved one; busy is the answer a caller retries (APP-COMMANDS rule 10).
        if (!_preferencesLoaded || _busy || _catalogStateUnreadable || _shuttingDown)
        {
            return AppCommandResponse.Busy(requestId, AppCommandMessages.Busy);
        }

        // SP-0279, item H: an address from --url-file is read off the UI thread and runs as if --url had carried it.
        var resolved = await AppUrlFile.ResolveAsync(requestId, parse.Options, request.WorkingDir,
            AppPlaybackMessages.PlayFileAddressNotLaunchable);
        if (resolved.Refusal is { } unreadable)
        {
            return unreadable;
        }

        var launch = AppPlaybackCommands.ToLaunchRequest(resolved.Options, request.WorkingDir);
        var (channel, refusal) = await ResolveTargetForCommandAsync(requestId, launch);
        if (channel is null)
        {
            return refusal ?? AppCommandResponse.Failed(requestId, AppCommandReason.Error, AppCommandExit.Failed, AppCommandMessages.Failed);
        }

        return await PlayAndWaitAsync(requestId, parse.Entry!, channel, AppPlaybackCommands.IsTrue(resolved.Options, "background"),
            cancellationToken);
    }

    private async Task<(StreamChannel? Channel, AppCommandResponse? Refusal)> ResolveTargetForCommandAsync(
        string requestId, StreamLaunchRequest launch)
    {
        switch (launch.Kind)
        {
            case StreamLaunchTargetKind.Url:
                return (CreateExternalChannel(launch.Url!), null);
            case StreamLaunchTargetKind.ChannelId:
                // The same resolution a shortcut gets (SP-0127): the id, else the row at the fallback address, else the
                // address itself when one was given.
                var found = StreamLaunchArguments.Resolve(_state.Channels, launch);
                if (found is null && launch.Url is { } fallback)
                {
                    _log.Event("LAUNCH", "op=resolve", "result=address_only");
                    found = CreateExternalChannel(fallback);
                }

                return found is null
                    ? (null, AppCommandResponse.Failed(requestId, AppCommandReason.TargetMissing, AppCommandExit.NotReady,
                        AppPlaybackMessages.ChannelNotFound))
                    : (found, null);
            case StreamLaunchTargetKind.File:
                return await ImportFileForCommandAsync(requestId, launch.File!);
            default:
                return (null, AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                    AppPlaybackMessages.PlayNeedsTarget));
        }
    }

    /// <summary>
    /// <c>play --file</c>: read, check and import the document exactly as a double-click does, but answering in words what
    /// the import answers with a message box. Every condition that box reports is tested here first, with no await
    /// between the last test and the import, so the import itself never reaches a modal.
    /// </summary>
    private async Task<(StreamChannel? Channel, AppCommandResponse? Refusal)> ImportFileForCommandAsync(string requestId, string path)
    {
        string text;
        try
        {
            text = await Task.Run(async () =>
            {
                var info = new FileInfo(path);
                return info.Length > BroadcastResourceReader.MaximumDocumentBytes
                    ? string.Empty
                    : await File.ReadAllTextAsync(path, Encoding.UTF8);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log.Event("RESOURCE IMPORT FAIL", "source=file", $"reason={exception.GetType().Name}");
            SetStatus("ImportFileReadFailed");
            return (null, AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                AppPlaybackMessages.FileUnreadable));
        }

        if (_busy)
        {
            _log.Event("REFUSE", "op=resource_import", "reason=busy");
            return (null, AppCommandResponse.Busy(requestId, AppCommandMessages.Busy));
        }

        var read = BroadcastResourceReader.Read(text);
        if (!read.IsAccepted || read.Resource is null)
        {
            _log.Event("RESOURCE IMPORT FAIL", $"status={read.Status}");
            var key = read.Status switch
            {
                BroadcastResourceStatus.UnsupportedVersion => "BroadcastResourceUnsupportedVersion",
                BroadcastResourceStatus.NotResourceDocument => "BroadcastResourceNotDocument",
                _ => "BroadcastResourceInvalid"
            };
            SetStatus(key);
            return (null, AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                AppPlaybackMessages.FileInvalid));
        }

        var imported = await ImportBroadcastResourceTextAsync(text, this);
        return imported is null
            ? (null, AppCommandResponse.Failed(requestId, AppCommandReason.Error, AppCommandExit.Failed, AppCommandMessages.Failed))
            : (imported, null);
    }

    /// <summary>
    /// The shared body of <c>play</c> and <c>playback resume</c>: refuse what needs a person, leave a stream that already
    /// plays alone (idempotent - the window's own play toggles a playing station off, which a command must never do),
    /// start the ordinary route quietly and wait for its verdict inside the verb's bound.
    /// </summary>
    private async Task<AppCommandResponse> PlayAndWaitAsync(
        string requestId, AppCommandEntry entry, StreamChannel channel, bool background, CancellationToken cancellationToken)
    {
        if (IsPasswordProtected(channel) && BroadcastPasswords.Load(channel.Id) is not { Length: > 0 })
        {
            // The window would ask; a command does not (rule 4). The person is told where they are looking.
            SetStatus("BroadcastPasswordRequired");
            return AppCommandResponse.Refused(requestId, AppCommandReason.NeedsPerson, AppCommandExit.Permission,
                AppPlaybackMessages.NeedsPassword);
        }

        if (FindPlaying(channel) is { } existing)
        {
            if (!background)
            {
                BringApplicationToFront();
                existing.Window?.Activate();
            }

            return AppPlaybackCommands.Playing(requestId, existing.Session);
        }

        if (!background)
        {
            _log.Event("COMMAND PLAY", $"foreground={BringApplicationToFront()}");
        }

        var watch = new CommandPlaybackWatch(channel.Id);
        _commandWatch = watch;
        try
        {
            await PlayChannelAsync(channel, rememberSelection: false, quiet: true, background: background);
            if (!HasOpenStream(channel.Id) && !watch.Settled.Task.IsCompleted)
            {
                // The route ended before it opened anything: offline, an address it refuses, a broadcast with no path.
                return AppPlaybackCommands.OpenFailed(requestId, NetworkInterface.GetIsNetworkAvailable()
                    ? PlaybackReachability.NotProbed
                    : PlaybackReachability.NetworkUnreachable);
            }

            // A margin under the second process's own bound, so the verdict that comes back is ours when it is ours to give.
            var bound = AppCommandPolicy.CompletionBound(entry) - TimeSpan.FromSeconds(1);
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var first = await Task.WhenAny(watch.Settled.Task, Task.Delay(bound, delay.Token));
            delay.Cancel();
            if (first != watch.Settled.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return AppCommandResponse.Failed(requestId, AppCommandReason.Timeout, AppCommandExit.NotProven,
                    AppPlaybackMessages.NoSoundInTime);
            }

            var settled = await watch.Settled.Task;
            if (!settled.Live)
            {
                return AppPlaybackCommands.OpenFailed(requestId, settled.Reachability);
            }

            var session = CommandSessions().FirstOrDefault(open => open.ChannelId == channel.Id) ??
                new AppPlaybackSession(
                    channel.MediaKind == MediaKind.Audio ? AppPlaybackSession.AudioKind : AppPlaybackSession.VideoKind,
                    channel.Id, StreamTitleFormatter.Display(channel.Title), AppPlaybackState.Playing, Recording: false);
            return AppPlaybackCommands.Playing(requestId, session);
        }
        finally
        {
            if (ReferenceEquals(_commandWatch, watch))
            {
                _commandWatch = null;
            }
        }
    }

    /// <summary><c>playback stop</c>: the audio session and the video windows the request names; nothing to stop is done.</summary>
    internal AppCommandResponse StopForCommand(string requestId, AppCommandParse parse)
    {
        Guid? channelId = parse.Value("id") is { } text && Guid.TryParse(text, out var parsed) ? parsed : null;
        var plan = AppPlaybackCommands.PlanStop(requestId, channelId, AppPlaybackCommands.IsTrue(parse.Options, "all"), CommandSessions());
        if (plan.Refusal is not null)
        {
            return plan.Refusal;
        }

        var stopped = 0;
        if (plan.Targets.Any(target => target.Kind == AppPlaybackSession.AudioKind))
        {
            StopAudio();
            stopped++;
        }

        var windows = _playerWindows
            .Where(candidate => !candidate.IsClosingForCommand &&
                                plan.Targets.Any(target => target.Kind == AppPlaybackSession.VideoKind && target.ChannelId == candidate.Channel.Id))
            .ToArray();
        foreach (var window in windows)
        {
            window.Close();
        }

        stopped += windows.Length;
        _log.Event("COMMAND STOP", $"stopped={stopped}");
        return AppCommandResponse.Done(requestId, AppPlaybackMessages.Stopped(stopped), new JsonObject { ["stopped"] = stopped });
    }

    /// <summary><c>playback pause</c>: the audio session only - neither the video backend nor the player window has a pause.</summary>
    internal AppCommandResponse PauseForCommand(string requestId)
    {
        if (_playingAudio is not null)
        {
            PauseAudio();
            return AppCommandResponse.Done(requestId, AppPlaybackMessages.Paused);
        }

        return _audioPausedChannelId is not null
            ? AppCommandResponse.Done(requestId, AppPlaybackMessages.AlreadyPaused)
            : AppCommandResponse.Failed(requestId, AppCommandReason.TargetMissing, AppCommandExit.NotReady, AppPlaybackMessages.NoRadio);
    }

    /// <summary><c>playback resume</c>: restarts the paused station at the live edge, as the transport button does.</summary>
    internal async Task<AppCommandResponse> ResumeForCommandAsync(
        string requestId, AppCommandEntry entry, CancellationToken cancellationToken)
    {
        if (_playingAudio is not null)
        {
            return AppPlaybackCommands.Playing(requestId, CommandSessions().First(session => session.Kind == AppPlaybackSession.AudioKind));
        }

        return PausedAudioChannel is { } paused
            ? await PlayAndWaitAsync(requestId, entry, paused, background: true, cancellationToken)
            : AppCommandResponse.Failed(requestId, AppCommandReason.TargetMissing, AppCommandExit.NotReady,
                AppPlaybackMessages.NoRadioToResume);
    }

    /// <summary><c>playback status</c>: what is open, with no address in it.</summary>
    internal AppCommandResponse StatusForCommand(string requestId)
    {
        var sessions = CommandSessions();
        return AppCommandResponse.Done(requestId, AppPlaybackMessages.Listed(sessions.Count), AppPlaybackCommands.SessionsData(sessions));
    }

    /// <summary>
    /// <c>volume set</c>: the radio's volume through the slider and its own save, or the video players' volume and mute
    /// through each open window and the one preference write the window itself uses - the pair <c>StandardSettings</c> names
    /// <c>audioVolume</c>, <c>videoVolume</c> and <c>videoMuted</c>.
    /// </summary>
    internal async Task<AppCommandResponse> SetVolumeForCommandAsync(string requestId, AppCommandParse parse)
    {
        // SP-0291 (S4-2): the guard channel add uses - before its load the slider's save is refused and the command
        // would answer done anyway; busy is the answer a caller retries (APP-COMMANDS rule 10).
        if (!_preferencesLoaded || _busy || _catalogStateUnreadable || _shuttingDown)
        {
            return AppCommandResponse.Busy(requestId, AppCommandMessages.Busy);
        }

        var level = int.Parse(parse.Value("level")!, CultureInfo.InvariantCulture);
        if (parse.Value("target") == "video")
        {
            var muted = parse.Has("muted")
                ? AppPlaybackCommands.IsTrue(parse.Options, "muted")
                : (_stateCommitter?.Requested ?? _state).VideoMuted;
            foreach (var window in _playerWindows.Where(candidate => !candidate.IsClosingForCommand).ToArray())
            {
                window.ApplyVolumeFromCommand(level, muted);
            }

            await SaveVideoAudioPreferencesAsync(level, muted);
        }
        else
        {
            AudioVolumeSlider.Value = level; // the engine and the compact panel follow through the slider's handler
            _audioVolumeSaveTimer.Stop();
            _pendingAudioVolume = level;
            await FlushPendingAudioVolumeAsync();
        }

        _log.Event("COMMAND VOLUME", $"target={(parse.Value("target") ?? "audio")}", $"level={level}");
        return AppCommandResponse.Done(requestId, AppPlaybackMessages.VolumeSet(level), new JsonObject { ["level"] = level });
    }
}
