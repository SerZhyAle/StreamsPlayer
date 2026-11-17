using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0272: how a player window ended its open - the first picture, or a terminal failure. The playback commands wait
/// on this; nothing else reads it.
/// </summary>
internal readonly record struct PlaybackSettlement(bool Live, string? Reason, PlaybackReachability Reachability);

/// <summary>SP-0273: what became of a <c>record start</c> on a player window.</summary>
internal enum PlayerRecordingStart
{
    /// <summary>The engine is writing the recording's file.</summary>
    Started,

    /// <summary>The window is closing, failed to open, or has not shown its first picture.</summary>
    NotLive,

    /// <summary>A start, a resume or a finish is waiting on the engine.</summary>
    Busy,

    /// <summary>The engine in use has no recording.</summary>
    Unavailable,

    /// <summary>The folder took no write when the window probed it.</summary>
    NoWritableFolder,

    /// <summary>The engine refused the request.</summary>
    Refused,

    /// <summary>The engine accepted, and its file was not seen in time (or the stream re-opened meanwhile).</summary>
    NotConfirmed,

    /// <summary>The session ended by itself before its file was seen.</summary>
    NotWriting
}

/// <summary>
/// SP-0272: the player window's end of the playback command group - the verdict of an open, the state
/// <c>playback status</c> reports, and the volume a <c>volume set</c> applies. The window's own behaviour is untouched.
/// SP-0273 adds the recording verbs' end: the folder a recording writes into, whether the engine is really writing, and
/// the start and the end of a recording with their verdicts - all on the code the Record button uses.
/// </summary>
public partial class PlayerWindow
{
    /// <summary>How long past the engine's own grace a start waits before it believes the engine's answer about its file.</summary>
    private static readonly TimeSpan RecordingConfirmMargin = TimeSpan.FromMilliseconds(300);

    /// <summary>The engine's file check lists a staging directory on the UI thread; the stats tick does it every 2 s, so this is gentle.</summary>
    private static readonly TimeSpan RecordingConfirmPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>The folder this window's recording writes into, or <see langword="null"/> when it records nothing or is finishing.</summary>
    internal string? RecordingFolderForCommand =>
        _recordingSession is { } session && !_recordingEnding ? session.Target.Folder : null;

    /// <summary>True while a recording of this window is being finished.</summary>
    internal bool IsRecordingEndingForCommand => _recordingSession is not null && _recordingEnding;

    /// <summary>
    /// Whether the engine is writing the recording's file now - its own truth, never the accepted request (SP-0164) - and the
    /// recording is not between two segments.
    /// </summary>
    internal bool IsRecordingWritingForCommand =>
        _recordingSession is not null && !_recordingEnding && !_recordingResumePending && _backend.IsRecording;

    /// <summary>
    /// <c>record start</c>: the Record button's own start, into exactly <paramref name="folder"/>, then the wait for the engine's
    /// file - "accepted" is the engine saying yes, and the first bytes are its file turning up in staging, which the engine's
    /// own probe allows a grace window for. A session the window ends or loses meanwhile is not a recording.
    /// </summary>
    internal async Task<PlayerRecordingStart> StartRecordingForCommandAsync(
        string folder, TimeSpan bound, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + bound;
        var started = await TryStartRecordingSessionAsync([folder]);
        if (started != PlayerRecordingStart.Started)
        {
            return started;
        }

        var session = _recordingSession;
        var confirmAt = DateTimeOffset.Now + RecordingWriteProbe.AppearGrace + RecordingConfirmMargin;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closing || session is null || !ReferenceEquals(_recordingSession, session))
            {
                return PlayerRecordingStart.NotWriting;
            }

            var now = DateTimeOffset.Now;
            if (now >= confirmAt && IsRecordingWritingForCommand)
            {
                return PlayerRecordingStart.Started;
            }

            if (now >= deadline)
            {
                return PlayerRecordingStart.NotConfirmed;
            }

            await Task.Delay(RecordingConfirmPoll, cancellationToken);
        }
    }

    /// <summary><c>record stop</c>: the Record button's own end; what became of every segment, or null when none was ended.</summary>
    internal Task<IReadOnlyList<SegmentResult>?> StopRecordingForCommandAsync() => EndRecordingSessionAsync(RecordingEnd.User);

    /// <summary>
    /// Raised on the UI thread at the first picture of a leg and at a terminal failure, whatever opened the window.
    /// </summary>
    internal event Action<PlayerWindow, PlaybackSettlement>? PlaybackSettled;

    /// <summary>The state <c>playback status</c> reports for this window.</summary>
    internal string CommandState =>
        _failureShown ? AppPlaybackState.Failed
        : _reachedLive && !_buffering && !_isStalled ? AppPlaybackState.Playing
        : AppPlaybackState.Buffering;

    /// <summary>True once the window has begun to close; such a window is no longer a session.</summary>
    internal bool IsClosingForCommand => _closing;

    /// <summary>Whether this window is recording its stream now.</summary>
    internal bool IsRecordingForCommand => _recordingSession is not null;

    /// <summary>
    /// Applies a level and the final mute state. The slider carries the level to the engine (and schedules the same
    /// save the window always makes); the mute state is asserted after it, because moving the slider above zero
    /// unmutes by itself and a command that said "muted" must stay muted.
    /// </summary>
    internal void ApplyVolumeFromCommand(int level, bool muted)
    {
        VolumeSlider.Value = level;
        _isMuted = muted;
        _backend.Mute = muted;
        UpdateMuteButton();
    }

    private void NotifySettled(bool live, string? reason = null, PlaybackReachability reachability = PlaybackReachability.NotProbed) =>
        PlaybackSettled?.Invoke(this, new PlaybackSettlement(live, reason, reachability));
}
