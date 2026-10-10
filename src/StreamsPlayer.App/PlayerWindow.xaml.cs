using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class PlayerWindow : Window
{
    // Fixed live buffer. Stalls on the tested streams were clock/decode faults, not starvation, so growing the buffer did not help.
    private const uint LiveCacheMilliseconds = 15_000;
    // Re-opens (end_reconnect/retry) refill this buffer before playback resumes. Flapping sources
    // (short/looping playlists that hit EndReached every ~20s) would otherwise show the 15s buffering
    // spinner on every reconnect. A smaller reconnect buffer keeps re-opens quick.
    private const uint ReconnectCacheMilliseconds = 4_000;
    // SP-0102: reduced from 10s to 4s for responsive playback experience
    private static readonly TimeSpan ControlsHideTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StatsSampleInterval = TimeSpan.FromSeconds(2);
    // Volume is applied to the engine on every slider move but persisted only once the slider settles:
    // a drag raises ValueChanged per pixel and each save rewrites the entire catalog state.
    private static readonly TimeSpan VolumeSaveDelay = TimeSpan.FromMilliseconds(600);
    // Part D stall watchdog: how often the engine is observed. What counts as a freeze, and for how long
    // it must last, is PlaybackFreezeDetector's answer (SP-0070), not this interval's.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _controlsHideTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _volumeSaveTimer;
    private readonly HashSet<ContextMenu> _openControlPanelMenus = [];
    private readonly Stopwatch _playbackClock = new();
    private readonly StreamChannel _channel;
    private readonly CurrentLog _log;
    private readonly Action<string, BitmapSource>? _onThumbnail;
    // SP-0038 / SP-0179: the user's folder per capture kind, read per save, not captured at open, so a folder
    // changed in Settings applies to the next capture of a player window that is already on screen.
    private readonly Func<CaptureKind, string?> _captureFolder;
    private bool _thumbnailCaptured;
    // Each engine result keeps the purpose of the request that created it.
    private readonly Dictionary<Guid, bool> _snapshotRequests = [];
    private volatile bool _closing;

    // SP-0034: this window is shown non-modally, so the language can change while it is open. Text
    // assigned from a formatted string has no DynamicResource to follow, so the key and its arguments
    // are kept and replayed by RefreshLocalization. A null key means the label is currently bound to a
    // resource and follows the swap on its own.
    private string? _waitResourceKey;
    private object?[] _waitArguments = [];

    private readonly Func<Guid, bool, Task> _recordOutcome;
    private readonly Func<StreamChannel, Task> _requestRemove;
    private readonly Func<bool, Task> _saveTopmost;
    private readonly Func<bool> _isPinned;
    // Pins/unpins this channel in the catalog; the owner (MainWindow) persists and re-filters.
    private readonly Func<bool, Task> _savePinned;
    // SP-0191: persists the keep-the-controls-visible preference; the owner (MainWindow) persists it
    // and applies it to the other open player windows.
    private readonly Func<bool, Task>? _saveKeepControlsVisible;
    private bool _controlsPinned;
    private readonly Func<IReadOnlyList<ChannelCollection>> _getCollections;
    private readonly Func<Guid, bool, Task> _saveCollectionMembership;
    private readonly Func<string, Task<bool>> _createCollection;
    private readonly Func<int, bool, Task> _saveAudioPreferences;
    private readonly bool _startFullscreen;
    // SP-0026: the selected video engine (LibVLC by default, FlyleafLib opt-in). The Play/teardown
    // race protection now lives inside the backend; this window drives engine-agnostic orchestration.
    private IVideoBackend _backend;
    private readonly MediaBackend _backendSelection;
    private readonly List<Task> _retiredBackendReleases = [];
    private Task _previousBackendRelease = Task.CompletedTask;
    private Action<float>? _bufferingHandler;
    private Action? _errorHandler;
    private Action? _endHandler;
    private Action? _tracksHandler;
    private Action<Guid, BitmapSource>? _snapshotHandler;
    private bool _outcomeRecorded;
    private bool _terminalFailureRecorded;
    // SP-0062: set for a window opened by the startup resume, and cleared the first time this window
    // reaches live. While set, a failure is recorded and logged but never raised as a dialog: at launch
    // several of them would stack in front of a catalog the user has not touched yet. Deliberately a
    // one-shot latch of its own rather than a test of _reachedLive, which StartMedia resets on every
    // recovery leg - reading that field instead would keep a long-lived resumed window silent for good.
    private bool _quietUntilLive;
    private bool _reachedLive;
    private bool _isStalled;
    private int _stallCount;
    // SP-0040: session-level quality accounting. _playbackClock restarts on every reconnect, so it
    // measures the current leg only; the archived log has to answer "how did this session as a whole
    // cope with a bad stream", which needs a clock and counters that survive the re-opens.
    private readonly Stopwatch _sessionClock = Stopwatch.StartNew();
    // Incremented by BeginLeg on the UI thread and read there by ObserveRendition, before the engine has the
    // new media - so a rendition belonging to the new leg is never filed under the previous one, which is
    // precisely what SP-0077's criterion 2 forbids.
    private int _legCount;
    private int _reconnectCount;
    private long _firstLiveMs = -1;
    private string _sessionOutcome = "closed";
    // SP-0015 bounded live recovery (policy lives in Core; this window feeds signals and applies decisions).
    private readonly LivePlaybackRecoveryPolicy _recovery = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly DispatcherTimer _watchdogTimer;
    private bool _recovering;        // label guard: a Reconnecting label is showing
    private bool _recoveryInFlight;  // re-entry guard: a decision for the current failure is being applied
    // SP-0070: the freeze decision itself lives in Core; this window only feeds it what it already
    // observes on the watchdog tick and applies the answer.
    private readonly PlaybackFreezeDetector _freeze = new();
    // SP-0096: the pre-live half of the supervision _freeze deliberately does not cover, and its exact
    // inverse in scope. Note the clocks are different on purpose: _freeze is fed HealthNow (the session
    // clock, which must span reconnects), this one is fed _playbackClock, which restarts per leg -
    // feeding it the session clock would expire the second re-open the instant it began.
    // SP-0203: the budget is recreated per attempt, because the dead-source slice is the current
    // endpoint's own connect bound - a LAN attempt moves on after 4 s, an exchange attempt keeps 8 s.
    private PlaybackOpenBudget _openBudget = new();
    // SP-0203: the attempt's own clock, restarted with every attempt (and every leg), is the input
    // the dead-source branch judges; the leg clock keeps the deadline leg-scoped.
    private bool _firstByteLogged;
    // SP-0096 criterion 6: a terminal failure is announced once. The budget expiring in the same second
    // as an engine error is a real race, and ShowFailureDialog is modal - a second call would stack a
    // dialog behind the first.
    private bool _failureShown;
    private bool _buffering;
    private bool _bufferFullPending;
    private long _bufferingSinceMs;
    private long _bufferingStartPosition;
    // The live buffer the media currently open was started with - the delay the live caption reports.
    // Written by BeginLeg and read by ShowLiveStatus, both on the UI thread.
    private uint _liveCacheMs = LiveCacheMilliseconds;
    private bool _settingsReady;
    private bool _isMuted;
    private bool _fullscreen;
    private WindowStyle _restoredWindowStyle;
    private ResizeMode _restoredResizeMode;
    private WindowState _restoredWindowState;
    private IDisposable? _wake;
    private string? _audioOutputDevice;
    private AudioChannelMode _audioChannelMode = AudioChannelMode.Stereo;

    internal PlayerWindow(
        StreamChannel channel,
        CurrentLog log,
        Func<Guid, bool, Task> recordOutcome,
        Func<StreamChannel, Task> requestRemove,
        Func<bool> isPinned,
        Func<bool, Task> savePinned,
        bool topmost,
        Func<bool, Task> saveTopmost,
        Func<IReadOnlyList<ChannelCollection>> getCollections,
        Func<Guid, bool, Task> saveCollectionMembership,
        Func<string, Task<bool>> createCollection,
        int volume,
        bool muted,
        Func<int, bool, Task> saveAudioPreferences,
        Action<string, BitmapSource>? onThumbnail,
        Func<CaptureKind, string?> captureFolder,
        MediaBackend backend,
        bool startFullscreen = false,
        bool quietUntilLive = false,
        string? audioOutputDevice = null,
        AudioChannelMode audioChannelMode = AudioChannelMode.Stereo,
        bool keepControlsVisible = false,
        Func<bool, Task>? saveKeepControlsVisible = null)
    {
        InitializeComponent();
        _channel = channel;
        _log = log;
        _onThumbnail = onThumbnail;
        _captureFolder = captureFolder;
        _recordOutcome = recordOutcome;
        _requestRemove = requestRemove;
        _isPinned = isPinned;
        _savePinned = savePinned;
        _saveTopmost = saveTopmost;
        _getCollections = getCollections;
        _saveCollectionMembership = saveCollectionMembership;
        _createCollection = createCollection;
        _saveAudioPreferences = saveAudioPreferences;
        _startFullscreen = startFullscreen;
        _quietUntilLive = quietUntilLive;
        _backendSelection = backend;
        _audioOutputDevice = audioOutputDevice;
        _audioChannelMode = audioChannelMode;
        _backend = VideoBackendFactory.Create(backend, volume, muted, log);
        _backend.AudioOutputDevice = audioOutputDevice;
        _backend.AudioChannelMode = audioChannelMode;
        VideoHost.Children.Add(_backend.View);
        // Move the control overlay out of the WPF root and into the backend's native video surface so
        // it floats above the video (airspace) and is not covered by the video on window resize.
        var overlayRoot = (Grid)ControlsOverlay.Parent;
        overlayRoot.Children.Remove(ControlsOverlay);
        _backend.SetOverlay(ControlsOverlay);
        VolumeSlider.Value = Math.Clamp(volume, 0, 100);
        _isMuted = muted;
        UpdateMuteButton();
        _controlsHideTimer = new DispatcherTimer { Interval = ControlsHideTimeout };
        _controlsHideTimer.Tick += ControlsHideTimer_Tick;
        // SP-0191: a pinned panel stays over the video (WINDOWS-UI 8.4); the preference is global and
        // reaches the other open player windows through MainWindow.
        _saveKeepControlsVisible = saveKeepControlsVisible;
        ApplyControlsPin(keepControlsVisible);
        _statsTimer = new DispatcherTimer { Interval = StatsSampleInterval };
        _statsTimer.Tick += StatsTimer_Tick;
        _volumeSaveTimer = new DispatcherTimer { Interval = VolumeSaveDelay };
        _volumeSaveTimer.Tick += VolumeSaveTimer_Tick;
        _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
        _watchdogTimer.Tick += WatchdogTimer_Tick;
        AttachBackendEvents(_backend);
        _backend.RecordingInterrupted += Backend_RecordingInterrupted; // SP-0121: kept through teardown on purpose
        UpdateRecordingUi(); // SP-0121: an engine that cannot record says so on the button from the start
        // SP-0076: started here rather than in Loaded so a local file read overlaps window layout instead
        // of standing in front of the first open. Video only - radio has no renditions to choose between,
        // so its first open must not pay a read that could never produce a ceiling.
        _qualityRecall = channel.MediaKind == MediaKind.Video
            ? QualityMemoryFile.RecallAsync(channel.Url, DateTimeOffset.UtcNow)
            : null;
        Topmost = topmost;
        _settingsReady = true;
        TitleText.Text = StreamTitleFormatter.Display(channel.Title);
        RefreshWindowTitle();
        Loaded += PlayerWindow_Loaded;
        Closed += PlayerWindow_Closed;
    }

    internal void ApplyAudioOutputSettings(string? deviceId, AudioChannelMode channelMode)
    {
        _audioOutputDevice = deviceId;
        _audioChannelMode = channelMode;
        _backend.AudioOutputDevice = deviceId;
        _backend.AudioChannelMode = channelMode;
    }

    /// <summary>Re-renders the text this window cannot express as a <c>DynamicResource</c>.</summary>
    internal void RefreshLocalization()
    {
        RefreshWindowTitle();
        if (_waitResourceKey is { } key)
        {
            WaitText.Text = LocalizationService.Format(key, _waitArguments);
        }

        RefreshSignalHealthText(); // SP-0045: the stripe's tooltip is assigned, not bound, so it replays here
        RefreshInterruptionNotice(); // SP-0072: same reason - one of its states carries numbers
        ApplyNowPlaying(); // SP-0073: same reason - the line wraps untranslated broadcast text in a translated one
    }

    // Stream name first so the taskbar button identifies the broadcast even when heavily truncated.
    private void RefreshWindowTitle() => Title = LocalizationService.Format(
        "WindowTitleWithSubject",
        TitleText.Text,
        LocalizationService.Get("PlayerWindowTitle"));

    private void SetWaitText(string resourceKey, params object?[] arguments)
    {
        _waitResourceKey = resourceKey;
        _waitArguments = arguments;
        WaitText.Text = LocalizationService.Format(resourceKey, arguments);
    }

    private void SetWaitTextResource(string resourceKey)
    {
        _waitResourceKey = null;
        _waitArguments = [];
        WaitText.SetResourceReference(TextBlock.TextProperty, resourceKey);
    }

    /// <remarks>
    /// SP-0076: <c>async void</c> because the remembered ceiling has to be in hand before the first open,
    /// and the read that produces it started in the constructor - so the await below almost always resumes
    /// inline and the first frame is no later than it was. The one thing it can introduce is a window that
    /// closed inside that gap, which the guard after it answers.
    /// </remarks>
    private async void PlayerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // The catalog lends this window its ownership for the CenterOwner placement only, and gets it back
            // here, at the first moment the placement is already applied. Windows minimizes and restores owned
            // windows together with their owner, so an owned player - a fullscreen one included, which is
            // exactly when the catalog gets minimized - went down with the list. From here on this is an
            // independent top-level window; MainWindow_Closing is what closes it when the catalog quits.
            // Before StartMedia: opening the stream holds the UI thread, and every millisecond of it is a
            // millisecond in which the window is on screen and still owned.
            Owner = null;
            // System + display wake for the video/RTSP session lifetime (Decision 3: the user is watching).
            // Tied to the window rather than LibVLC's thread-affine, flapping play/pause events, so the
            // hold survives bounded reconnects and is released reliably in PlayerWindow_Closed.
            _wake = WakeGuard.Acquire(keepDisplayOn: true);
            ApplySignalHealth(); // SP-0045: the colourless opening state, before any claim can be made
            // SP-0072: the blackout starts here, before the open, so its delay is measured from the moment
            // the window went black rather than from the first event the engine happens to raise.
            NotifyInterrupted(PlaybackInterruptionKind.Connecting);
            // SP-0076: the ladder is still read only after this open, but what earlier sessions recorded about
            // this source is a local file and arrives in time to shape it.
            await ApplyRememberedCeilingAsync();
            if (_closing)
            {
                return; // the window was closed inside the read; PlayerWindow_Closed has already torn down
            }

            // SP-0096: the observation cadence starts before the leg. Its open clock is reset below,
            // and the native call runs on a worker, so a slow engine cannot defer the first observation.
            _statsTimer.Start();
            _watchdogTimer.Start();
            _ = StartMediaOffUiThreadAsync("initial", QualityCeiling); // the ladder's answer is not in yet - memory's may be
            ShowControls();
            if (_startFullscreen)
            {
                ToggleFullscreen();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(PlayerWindow_Loaded), exception);
        }
    }

    private const int IconWidth = 480;

    /// <summary>SP-0053: the channel this window is playing, so the About window can be offered for it.</summary>
    internal StreamChannel Channel => _channel;

    /// <summary>
    /// SP-0120: completes once this window's engine has been released after it closed - what quitting waits for
    /// (bounded) so the process does not exit in the middle of a native teardown. Completed until then.
    /// </summary>
    internal Task EngineReleased { get; private set; } = Task.CompletedTask;

    /// <summary>SP-0053: this window's engine already has the description; nothing is opened for it.</summary>
    internal StreamTransmission? DescribeTransmission() => _backend.DescribeTransmission();

    private bool CaptureThumbnail() => RequestSnapshot(IconWidth, manual: false);

    private bool RequestSnapshot(int width, bool manual)
    {
        if (_snapshotRequests.Count >= 32)
        {
            _snapshotRequests.Clear(); // discard abandoned native requests rather than grow without bound
        }

        var requestId = Guid.NewGuid();
        _snapshotRequests.Add(requestId, manual);
        if (_backend.RequestSnapshot(requestId, width))
        {
            return true;
        }

        _snapshotRequests.Remove(requestId);
        return false;
    }

    // SP-0038: one press, two effects - the frame on screen is written to a picture file the user owns
    // and adopted as this channel's grid icon (SP-0024). Zero asks both backends for the stream's own
    // resolution: the file is the point of the feature, and the icon is downscaled from the same frame.
    private void SaveFrameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_reachedLive)
        {
            return; // no frame has rendered yet - stay silent (AC 3)
        }

        RequestSnapshot(0, manual: true);
    }

    private void Backend_SnapshotReady(IVideoBackend source, Guid requestId, BitmapSource frame)
    {
        // Hand off on the UI thread; freezing here is what makes the image safe to encode from a worker.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !ReferenceEquals(source, _backend)
                || !_snapshotRequests.Remove(requestId, out var manual))
            {
                return;
            }

            frame = Frozen(frame);
            if (!manual)
            {
                _onThumbnail?.Invoke(_channel.Url, frame);
                return;
            }

            _onThumbnail?.Invoke(_channel.Url, ToIconSize(frame));
            HandlerBoundary.Run(nameof(SaveFrameFileAsync), () => SaveFrameFileAsync(frame));
        });
    }

    /// <summary>
    /// A frame the file writer can encode from a worker thread. Everything downstream - the icon store
    /// and the JPG encoder - touches the image off the UI thread, and an unfrozen WPF image belongs to
    /// the thread that made it; a source that refuses to freeze is copied into one that will.
    /// </summary>
    private static BitmapSource Frozen(BitmapSource frame)
    {
        if (frame.IsFrozen)
        {
            return frame;
        }

        if (frame.CanFreeze)
        {
            frame.Freeze();
            return frame;
        }

        var copy = new WriteableBitmap(frame);
        copy.Freeze();
        return copy;
    }

    /// <summary>
    /// The icon store expects a tile-sized picture, so a stream-resolution capture is scaled down here
    /// rather than being captured twice - a second snapshot would be a different moment.
    /// </summary>
    private static BitmapSource ToIconSize(BitmapSource frame)
    {
        if (frame.PixelWidth <= IconWidth)
        {
            return frame;
        }

        var scale = (double)IconWidth / frame.PixelWidth;
        var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private async Task SaveFrameFileAsync(BitmapSource frame)
    {
        try
        {
            var chain = CaptureFolders.Chain(CaptureKind.VideoFrame, _captureFolder(CaptureKind.VideoFrame));
            var (path, skipped) = await CapturedFrameWriter.SaveAsync(
                frame, chain, StreamTitleFormatter.Display(_channel.Title), DateTimeOffset.Now);
            _log.Event("FRAME SAVE", "ok=true", $"size={frame.PixelWidth}x{frame.PixelHeight}", $"path={path}", $"skipped={skipped ?? "none"}");
            // CAPTURE-OUTPUT rule 11: a fallback is told in the same moment, naming both folders.
            if (skipped is null)
            {
                ShowFrameToast(LocalizationService.Format("FrameSaved", Path.GetFileName(path)));
            }
            else
            {
                ShowFrameToast(LocalizationService.Format("FrameSavedElsewhere", skipped, path), RecordingNoticeHoldMs);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // A folder that vanished, went read-only, or filled up is the user's to fix; the window must
            // keep playing and say so rather than take down the session.
            _log.Event("FRAME SAVE", "ok=false", $"err={exception.Message}");
            ShowFrameToast(LocalizationService.Get("FrameSaveFailed"));
        }
    }

    // ~2s over-video confirmation: fade in, hold, fade out. Independent of the auto-hiding control panel
    // and IsHitTestVisible=false, so it stays legible (and unobtrusive) in fullscreen (AC 4).
    // SP-0121: a recording notice can name a folder or a place a file was left, so it may ask to be held longer.
    private void ShowFrameToast(string message, int holdMilliseconds = 1450)
    {
        FrameToastText.Text = message;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250 + holdMilliseconds))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550 + holdMilliseconds))));
        FrameSavedToast.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // SP-0045 rides this existing tick rather than adding one: the observation cadence is the budget.
    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (_failureShown)
        {
            // SP-0120: the engine was stopped with the verdict, so there is nothing left to observe - the tick
            // is kept only because it is what lets the Unavailable caption's appear delay elapse (SP-0072).
            ApplyInterruptionNotice();
            return;
        }

        _backend.LogStats("STATS");
        if (_bufferFullPending && _backend.ReadProgressCounters() is { DisplayedPictures: > 0 } shown)
        {
            NoteFirstDisplayedFrame(shown);
            UpdateBuffering(100f);
        }
        SampleSignalHealth();
        ObserveQuality(); // SP-0071 rides this tick too: the probe clock, no timer of its own
        ObserveRendition(); // SP-0077 rides it as well: what the engine actually put on screen
        ApplyInterruptionNotice(); // SP-0072 rides it as well: what makes the appear delay elapse
        SampleNowPlaying(); // SP-0073 rides it too: the stream's own "what is on air", no poll of its own
        // SP-0096 rides it last: a verdict raised here must replace the caption ApplyInterruptionNotice
        // just put up, not be overwritten by it. This tick and not the watchdog's because 2 s divides
        // the 8 s threshold and 3 s does not - the watchdog would report a dead source a second late.
        ObserveOpenBudget();
        ObserveRecording(); // SP-0121 rides it too: the badge timer, the engine-state check and the resume retry
    }

    /// <summary>
    /// SP-0096: the pre-live supervision. Deliberately the mirror image of <see cref="WatchdogTimer_Tick"/>'s
    /// guard - that one returns until the stream has been live, this one returns once it has - so between
    /// them the two rules cover the whole session with no overlap and no gap.
    /// </summary>
    private void ObserveOpenBudget()
    {
        if (_closing || _recoveryInFlight || _reachedLive)
        {
            return;
        }

        var received = _backend.ReadReceivedBytes();
        // SP-0203: the dead-source branch judges the attempt's own clock, the deadline the leg's -
        // a list of silent endpoints may not stretch one leg without end.
        var sinceAttempt = TimeSpan.FromMilliseconds(_attemptClock.ElapsedMilliseconds);
        var sinceLeg = TimeSpan.FromMilliseconds(_playbackClock.ElapsedMilliseconds);
        var verdict = _openBudget.Observe(sinceLeg, sinceAttempt, received);
        if (!_firstByteLogged && received is { } firstBytes && firstBytes > 0)
        {
            // Open question 1: time to first byte is recorded apart from time to first picture, and
            // neither is claimed against a budget.
            _firstByteLogged = true;
            _log.Event("PLAYBACK FIRST BYTE",
                $"attempt={_attemptIndex}",
                $"at_ms={sinceAttempt.TotalMilliseconds:F0}",
                $"url={AttemptUrl()}");
        }

        if (verdict == PlaybackOpenVerdict.None)
        {
            return;
        }

        _log.Event("PLAYBACK GIVEUP",
            $"rule={(verdict == PlaybackOpenVerdict.DeadSource ? "dead_source" : "deadline")}",
            $"at_ms={_playbackClock.ElapsedMilliseconds}",
            $"leg={_legCount}",
            $"attempt={_attemptIndex}",
            $"bytes={received?.ToString() ?? "n/a"}",
            $"url={_channel.Url}");

        // SP-0130: a probe's leg that never went live is a failed probe, not a dead channel - neither
        // verdict below applies to it, and the rung it left is re-opened instead.
        if (TryAbandonFailedProbe(verdict == PlaybackOpenVerdict.DeadSource ? "dead_source" : "deadline"))
        {
            return;
        }

        // SP-0203: this attempt said nothing (or outlived the leg); the producer ranked another one
        // behind it. The move costs no recovery budget and restarts the attempt's own supervision.
        if (CanAdvanceAttempt())
        {
            _ = AdvanceAttemptAsync(_backend, verdict == PlaybackOpenVerdict.DeadSource ? "attempt_dead_source" : "attempt_deadline");
            return;
        }

        if (verdict == PlaybackOpenVerdict.DeadSource)
        {
            // Straight to the verdict, not through RecoverAsync with a hard-fail signal. Two reasons,
            // both load-bearing: RecoverAsync would spend a PlaybackStatusProbe round trip on a host
            // that has just proved it answers nothing - adding seconds to the very wait this rule
            // exists to cut - and the owner's decision was no re-open attempt at all, which a
            // PLAYBACK RECOVER line would misreport. Nothing touches the remembered quality ceiling
            // either: a host that answered nothing said nothing about which rung was to blame.
            ShowPlaybackFailure("open_dead_source");
            return;
        }

        _ = RecoverAsync(new PlaybackFailureSignal("open_timeout", OpenTimedOut: true));
    }

    // Backend raises EndReached on its own thread; hop to the UI thread before driving recovery.
    private void Backend_EndReached(IVideoBackend source)
    {
        // A live stream reporting EndReached has usually just dropped; route it through the bounded recovery
        // policy (re-opening a live HLS stream naturally re-anchors to the live edge). Cancellable via _sessionCts.
        // SP-0203: before the first picture an end is the attempt's failure, like an engine error - recovery
        // restarts the list from the top, which would retry an endpoint that accepts and closes at once
        // until the budget is spent and never reach the one ranked behind it. A live leg is unchanged.
        var epoch = ReadAttemptEpoch();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && !_failureShown && ReferenceEquals(source, _backend) &&
                IsCurrentAttemptReport(epoch, "end_reached"))
            {
                AdvanceOrRecover("end_reached", new PlaybackFailureSignal("end_reached", EndReached: true));
            }
        });
    }

    private void Backend_BufferingChanged(IVideoBackend source, float cache) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(source, _backend))
            {
                UpdateBuffering(cache);
            }
        });

    private void UpdateBuffering(float cache)
    {
        if (_closing || _failureShown)
        {
            // SP-0120: queued by the engine before the window closed, run after teardown began. It reads the
            // position, the statistics and the track list; the backend answers those neutrally once released,
            // but a closed window has no buffering state worth updating either.
            return;
        }

        var percentage = Math.Clamp((int)Math.Round(cache), 0, 100);
        BufferProgress.Value = percentage;
        if (percentage < 100)
        {
            _bufferFullPending = false;
            if (!_buffering)
            {
                _buffering = true;
                _bufferingSinceMs = _playbackClock.ElapsedMilliseconds;
                _bufferingStartPosition = _backend.PositionMs;
            }

            // A plain buffer fill shows "Buffering… %"; an active recovery keeps its "Reconnecting…" label.
            if (!_recovering)
            {
                SetWaitText("BufferingProgress", percentage);
            }

            if (_reachedLive)
            {
                // SP-0045: reported per sample, not once per stall, so a long rebuffer keeps restarting
                // the clean interval instead of turning green in the middle of itself.
                _health.NotifyDisturbance(HealthNow);
                ApplySignalHealth();
                if (!_recovering)
                {
                    // SP-0072: the buffer emptied under a stream that was playing. Guarded on _recovering
                    // for the same reason the label above is: a recovery already owns the caption and
                    // names its attempt, which is the more useful of the two truths.
                    NotifyInterrupted(PlaybackInterruptionKind.SignalLost);
                }
            }

            // SP-0072: the backend raises this event continuously through a buffer fill, which gives the
            // caption a far finer cadence than the stats tick in exactly the state it reports.
            ApplyInterruptionNotice();

            if (_reachedLive && !_isStalled)
            {
                _isStalled = true;
                _stallCount++;
                _log.Event("PLAYBACK STALL", $"cache={percentage}", $"count={_stallCount}", $"at_ms={_playbackClock.ElapsedMilliseconds}", $"cache_ms={_liveCacheMs}", $"url={_channel.Url}");
                _backend.LogStats("STALL STATS");
                // SP-0071: the buffer emptied on a stream that was playing - the one measurement that says
                // this source is not delivering the current rung in real time. Nothing else re-opens here,
                // so a decision taken now has to open the media itself.
                NotifyQualityStarvation("stall", reopenNow: true);
            }

            return;
        }

        // LibVLC fills the buffer before it displays a picture. A leg has not reached video LIVE
        // until its own displayed-picture counter moves; otherwise a missing decoder can look healthy.
        if (_channel.MediaKind == MediaKind.Video && _backend.ReadProgressCounters() is { } progress)
        {
            if (progress.DisplayedPictures == 0)
            {
                _bufferFullPending = true;
                return;
            }

            NoteFirstDisplayedFrame(progress);
        }

        _bufferFullPending = false;
        _buffering = false;
        _recovering = false; // reached live - clear any Reconnecting label
        ShowLiveStatus();
        // SP-0072: the repo's own definition of "the picture is back" - this is where PLAYBACK LIVE is
        // logged, and the ticket's 3-18 s blackouts were measured to that line.
        NotifyPictureLive();
        ResumeRecordingIfPending(); // SP-0121: the picture is back, so the next segment can start
        RefreshTrackControls();
        if (_isStalled)
        {
            _isStalled = false;
            _log.Event("PLAYBACK RESUME", $"count={_stallCount}", $"at_ms={_playbackClock.ElapsedMilliseconds}", $"url={_channel.Url}");
            _backend.LogStats("RESUME STATS");
        }

        if (!_reachedLive)
        {
            _reachedLive = true;
            _probeLegPending = false; // SP-0130: the probe went live; its trial is the governor's from here
            _quietUntilLive = false; // SP-0062: from here on this is an ordinary window
            _recovery.NotifyLive(); // sustained live - restore the full recovery budget
            // SP-0096: the picture is on the screen, so this stream is the freeze rule's from here.
            // Told rather than tested, so a stream that went live half a second before its deadline
            // cannot be taken off the screen by the observation that follows.
            _openBudget.NotifyLive();
            // SP-0045: leaves red; an undisturbed first connect is green here, a stream returning from a
            // reconnect passes through yellow and earns green on the clean interval (decision 8).
            _health.NotifyLive();
            ApplySignalHealth();
            _log.Event("PLAYBACK LIVE", $"ttff_ms={_playbackClock.ElapsedMilliseconds}", $"url={_channel.Url}");
            if (_firstLiveMs < 0)
            {
                _firstLiveMs = _sessionClock.ElapsedMilliseconds;
            }

            _sessionOutcome = "live";
            RequestQualityLadder(); // SP-0071: after live, so the measurement never delays the first open
            if (!_outcomeRecorded)
            {
                _outcomeRecorded = true;
                _ = _recordOutcome(_channel.Id, true);
            }
            StartSmokeRecordingIfAsked(); // SP-0121: the playback smoke gate records through the shipping binary
            if (!_thumbnailCaptured && _onThumbnail is not null)
            {
                _thumbnailCaptured = true;
                _ = CaptureThumbnailSoonAsync();
            }
        }
    }

    /// <summary>
    /// The live caption, carrying how far behind the source this window is playing. The number is the live
    /// buffer the current leg was opened with - the delay the player itself adds, and the only part of the
    /// distance to the live edge it can state without asking the source anything - in whole seconds. It is
    /// therefore a floor rather than the whole truth: a segmented source adds its own pipeline on top.
    /// <para>The caption is re-assigned only when what it would say changes. The engine raises the
    /// buffering event that leads here continuously on a healthy stream, while the delay moves only when a
    /// re-open swaps the buffer (the initial 15 s for the reconnect's 4 s), so without this guard the label
    /// would be reformatted several times a second to say what it already says. The guard tests the
    /// rendered label rather than a remembered number, so a Buffering or Reconnecting caption that
    /// overwrote it is always restored.</para>
    /// </summary>
    private void ShowLiveStatus()
    {
        var seconds = (int)Math.Round(_liveCacheMs / 1000.0, MidpointRounding.AwayFromZero);
        if (_waitResourceKey == "PlayingLive" && _waitArguments is [int shown] && shown == seconds)
        {
            return;
        }

        SetWaitText("PlayingLive", seconds);
    }

    private async Task CaptureThumbnailSoonAsync()
    {
        await Task.Delay(700); // let a real frame render before snapshotting so a quick open->close still captures it
        if (!_closing)
        {
            CaptureThumbnail();
        }
    }

    private void Backend_EncounteredError(IVideoBackend source)
    {
        // SP-0203: the attempt the engine raised this under, read before the hop - a duplicate or a late
        // error of an attempt the leg has already moved past must not fail the endpoint that replaced it.
        var epoch = ReadAttemptEpoch();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && !_failureShown && ReferenceEquals(source, _backend) &&
                IsCurrentAttemptReport(epoch, "engine_error"))
            {
                AdvanceOrRecover("engine_error", new PlaybackFailureSignal("encountered_error"));
            }
        });
    }

    private void Backend_TracksChanged(IVideoBackend source) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && !_failureShown && ReferenceEquals(source, _backend))
            {
                RefreshTrackControls();
            }
        });

    private void RefreshTrackControls()
    {
        if (_closing)
        {
            return; // SP-0120: a TracksChanged queued before the window closed
        }

        AudioTracksButton.Visibility = _backend.AudioTracks.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        SubtitleTracksButton.Visibility = _backend.SubtitleTracks.Count > 1 ? Visibility.Visible : Visibility.Hidden;
    }

    private void AudioTracksButton_Click(object sender, RoutedEventArgs e) =>
        OpenTrackMenu(AudioTracksButton, _backend.AudioTracks, _backend.SelectedAudioTrackId, _backend.SelectAudioTrack);

    private void SubtitleTracksButton_Click(object sender, RoutedEventArgs e) =>
        OpenTrackMenu(SubtitleTracksButton, _backend.SubtitleTracks, _backend.SelectedSubtitleTrackId, _backend.SelectSubtitleTrack);

    private void OpenTrackMenu(
        Button button,
        IReadOnlyList<VideoTrack> tracks,
        int selectedTrackId,
        Action<int> selectTrack)
    {
        var menu = new ContextMenu();
        foreach (var track in tracks)
        {
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(track.Name) ? track.Id.ToString() : track.Name,
                IsCheckable = true,
                IsChecked = track.Id == selectedTrackId,
                Tag = track.Id
            };
            item.Click += (_, _) => selectTrack((int)item.Tag);
            menu.Items.Add(item);
        }

        ShowControlPanelMenu(button, menu);
    }

    private void ShowControlPanelMenu(Button button, ContextMenu menu)
    {
        menu.PlacementTarget = button;
        button.ContextMenu = menu;
        menu.Opened += (_, _) =>
        {
            _openControlPanelMenus.Add(menu);
            ShowControls();
        };
        menu.Closed += (_, _) =>
        {
            _openControlPanelMenus.Remove(menu);
            ShowControls();
        };
        menu.IsOpen = true;
    }

    // Drives the Part D recovery policy: classify the interruption, then either reconnect after a bounded,
    // cancellable backoff (keeping the Reconnecting label visible) or hand off to the terminal failure dialog.
    private async Task RecoverAsync(PlaybackFailureSignal signal)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => _ = RecoverAsync(signal));
            return;
        }

        if (_closing || _recoveryInFlight)
        {
            return; // window tearing down, or a decision for this same failure is already being applied
        }

        if (_failureShown)
        {
            // SP-0096: the verdict is final. Before SP-0120 stopped the engine with the verdict, a dead host
            // kept raising EncounteredError for as long as the dialog stood - observed in phase 6: a
            // dead_source give-up at 8 s was followed 23 s later by Reconnect attempt=1 against the very URL
            // just declared dead. Errors queued before the stop still arrive, so giving up still has to mean
            // it here. A hand retry is the one way back, and it clears this latch itself.
            return;
        }

        // SP-0130: an error or end on a probe's leg before live spends no recovery budget and never reaches
        // the verdict - the player returns to the rung that was playing.
        if (TryAbandonFailedProbe(signal.Reason ?? "error"))
        {
            return;
        }

        _recoveryInFlight = true;
        _recovering = true;
        // SP-0045: red once the stream has played at least once; before that it is still connecting and
        // the monitor keeps it colourless, so an ordinary open that retries never flashes red.
        _health.NotifyRecovering(HealthNow);
        ApplySignalHealth();
        // SP-0072: said before the status probe below, which is a network round trip and can take
        // seconds. That phase is precisely "the signal is gone and the player is looking"; leaving the
        // caption on the previous cause until a decision exists would be silent for exactly as long as
        // the probe runs, and an error that arrived without a rebuffer would leave it silent entirely.
        NotifyInterrupted(PlaybackInterruptionKind.SignalLost);
        try
        {
            // Only a fresh http/https open failure needs the status probe; stall/end/live-window already
            // carry their signal, and so does SP-0096's open verdict - the source has just had its full
            // twenty seconds to answer, so asking again buys nothing but more of the wait being cut.
            // SP-0041: the same fresh-open condition selects the connectivity gate, so a stream that was
            // already playing (stall, end, behind-live) is never gated and gains no latency (Decision 6).
            // SP-0203 (rule 5): a broadcast address is one listener slot - the probe pair never opens a
            // second request to it; the recovery policy runs on the signal the leg itself produced.
            var enriched = signal;
            var reachability = PlaybackReachability.NotProbed;
            if (signal.HttpStatusCode is null && !signal.Stall && !signal.EndReached && !signal.BehindLiveWindow && !signal.OpenTimedOut &&
                !FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(_channel))
            {
                reachability = await StreamReachabilityProbe.ProbeAsync(_channel.Url, _sessionCts.Token);
                if (_closing)
                {
                    return; // window closed while probing - do not touch the UI or restart
                }

                _log.Event("PLAYBACK REACH", $"verdict={reachability}", $"url={_channel.Url}");
                // A host that refused the connection cannot answer a status request either; skipping it
                // saves that probe's own timeout.
                if (PlaybackReachabilityRules.SpendsRecoveryBudget(reachability))
                {
                    enriched = signal with { HttpStatusCode = await PlaybackStatusProbe.TryGetStatusAsync(_channel.Url, _sessionCts.Token) };
                }
            }

            if (_closing)
            {
                return; // window closed while probing - do not touch the UI or restart
            }

            if (!PlaybackReachabilityRules.SpendsRecoveryBudget(reachability))
            {
                // Decisions 3 and 4: the policy is never consulted, so no attempt is spent and no counter
                // moves; the verdict is shown now instead of after a ladder that could not succeed.
                _recovering = false;
                ShowPlaybackFailure(enriched.Reason ?? "unreachable", reachability: reachability);
                return;
            }

            var decision = _recovery.Decide(enriched);
            _log.Event("PLAYBACK RECOVER",
                $"trigger={decision.Trigger}",
                $"action={decision.Kind}",
                $"attempt={decision.Attempt}",
                $"budget={decision.Budget}",
                $"delay_ms={decision.Delay.TotalMilliseconds:F0}",
                $"reason={enriched.Reason}",
                $"http={enriched.HttpStatusCode?.ToString() ?? "n/a"}",
                $"url={_channel.Url}");

            if (decision.Kind == RecoveryActionKind.HardFail)
            {
                _recovering = false;
                ShowPlaybackFailure(enriched.Reason ?? "recover_exhausted");
                return;
            }

            SetWaitText("ReconnectingAttempt", decision.Attempt, decision.Budget);
            // SP-0072: the same fact, in the layer the panel's auto-hide cannot take away. One blackout,
            // so this replaces the text in place instead of restarting the appear delay.
            NotifyInterrupted(PlaybackInterruptionKind.Reconnecting, decision.Attempt, decision.Budget);
            try
            {
                await Task.Delay(decision.Delay, _sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // stop / close / switch cancelled the wait - never restart the old stream
            }

            if (_closing)
            {
                return;
            }

            _reconnectCount++;
            // SP-0076: a session re-opening without ever having played is the one case where the ceiling
            // that came from memory is a likelier cause than the source.
            DropRememberedCeilingOnMiss();
            // SP-0071: read here, on the UI thread and as late as possible - the governor may have moved
            // the ceiling during the backoff above, and this re-open is what carries the new one.
            var ceiling = QualityCeiling;
            // Play off the UI thread (the backend serializes play against teardown) so a flapping stream never freezes WPF.
            await StartMediaOffUiThreadAsync("recover", ceiling);
        }
        finally
        {
            _recoveryInFlight = false;
        }
    }

    // Part D stall watchdog for silent freezes (no error thrown). Genuine rebuffering - data still
    // arriving, or pictures still reaching the screen - recovers in place (tuning §4); only a stream that
    // has stopped on both counts is torn down and re-prepared.
    private void WatchdogTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || _recoveryInFlight || !_reachedLive)
        {
            return;
        }

        var position = _backend.PositionMs;
        var progress = _backend.ReadProgressCounters();
        NoteFirstDisplayedFrame(progress); // SP-0133: the picture really reached the screen

        // Freeze A: nothing is reaching the screen and nothing is arriving from the source (SP-0070).
        // The rule is in Core; this tick is only its observation cadence, and the threshold is a duration
        // there rather than a poll count here.
        if (_freeze.Observe(HealthNow, _backend.IsPlaying, position, progress))
        {
            _log.Event("PLAYBACK WATCHDOG", "kind=frozen", $"pos_ms={position}", $"url={_channel.Url}");
            _health.NotifyDisturbance(HealthNow); // SP-0045: a caught freeze is a disturbance in its own right
            // SP-0071: a caught freeze is starvation too. reopenNow: false because the recovery below
            // re-opens anyway and reads the ceiling then - here the step down costs nothing extra.
            NotifyQualityStarvation("freeze", reopenNow: false);
            _ = RecoverAsync(new PlaybackFailureSignal("stall_frozen", Stall: true));
            return;
        }

        // Freeze B: buffering longer than 15 s with no position progress (a stuck buffer, not a live rebuffer).
        if (_buffering)
        {
            var bufferingMs = _playbackClock.ElapsedMilliseconds - _bufferingSinceMs;
            if (bufferingMs > 15_000 && (position < 0 || position - _bufferingStartPosition < 500))
            {
                _log.Event("PLAYBACK WATCHDOG", "kind=stuck_buffer", $"buffering_ms={bufferingMs}", $"url={_channel.Url}");
                _health.NotifyDisturbance(HealthNow); // SP-0045: a caught freeze is a disturbance in its own right
                _ = RecoverAsync(new PlaybackFailureSignal("stall_buffer", Stall: true));
            }
        }
    }

    private void ShowPlaybackFailure(
        string reason,
        bool notifyUser = true,
        PlaybackReachability reachability = PlaybackReachability.NotProbed)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowPlaybackFailure(reason, notifyUser, reachability));
            return;
        }

        if (_closing)
        {
            // SP-0119: a late verdict from an engine that was torn down because the user closed this window
            // (a disposed backend answers Play with false). The user ended the session, not the channel: no
            // failure record for a channel that may be healthy, and no dialog owned by a closed window,
            // which WPF refuses with an exception.
            _log.Event("PLAYBACK FAIL IGNORED", $"reason={reason}", "why=window_closing", $"url={_channel.Url}");
            return;
        }

        if (_failureShown)
        {
            // SP-0096 criterion 6: the open budget expiring in the same second as an engine error is a
            // real race, and _recoveryInFlight guards only the re-open path. Announcing the same dead
            // channel twice would stack a second modal dialog behind the first.
            return;
        }

        _failureShown = true;
        ReleaseAfterFailure();
        SetWaitTextResource("PlayerUnavailable");
        // SP-0072: a terminal failure is a black screen too, and it is the one the user can be left
        // staring at after dismissing the dialog - or that raises no dialog at all under _quietUntilLive.
        NotifyInterrupted(PlaybackInterruptionKind.Unavailable);
        // SP-0073 acceptance 3: the broadcast is over, so what was on it stops being said. This is the
        // only ending this window outlives - a channel change opens a different window, and a stop closes
        // this one.
        ClearNowPlaying();
        // SP-0045 acceptance 5: red behind the failure dialog, including for a channel that never played.
        _health.NotifyFailed(HealthNow);
        ApplySignalHealth();
        _sessionOutcome = "failed";
        _log.Event("PLAYBACK FAIL", $"reason={reason}", $"at_ms={_playbackClock.ElapsedMilliseconds}", $"kind={_channel.MediaKind}", $"url={_channel.Url}");
        if (!_terminalFailureRecorded)
        {
            _terminalFailureRecorded = true;
            _ = _recordOutcome(_channel.Id, false);
        }

        if (notifyUser && !_quietUntilLive)
        {
            ShowFailureDialog(reason, reachability);
        }
    }

    private void ShowFailureDialog(string reason, PlaybackReachability reachability = PlaybackReachability.NotProbed)
    {
        var report = FailureReportFormatter.Format(new FailureReport(
            ProductInfo.Version,
            DateTimeOffset.UtcNow,
            _channel.Title,
            _channel.Url,
            _channel.MediaKind,
            PlaybackErrorClassifier.Classify(reason)));
        var unlaunchable = reason == "unsupported_address";
        var dialog = new PlaybackFailureDialog(
            _channel.Title, _channel.SourceOrigin, report, _channel.Access,
            message: unlaunchable
                ? LocalizationService.Format("PlaybackAddressNotLaunchable", StreamTitleFormatter.Display(_channel.Title))
                : null,
            canRetry: !unlaunchable,
            reachability: reachability) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case PlaybackFailureChoice.Retry:
                _recovery.Reset(); // a manual retry starts a fresh recovery budget
                _recovering = false;
                // SP-0071: the recovery budget is reset, the ceiling is not. What the governor learned
                // about this source's delivery did not stop being true because the user pressed Retry.
                // SP-0076: a ceiling that came from memory and never produced a picture is the exception -
                // the hand retry is exactly the moment to stop insisting on a week-old opinion.
                DropRememberedCeilingOnMiss();
                NotifyInterrupted(PlaybackInterruptionKind.Connecting); // SP-0072: connecting again, by hand
                _ = RetryAfterFailureAsync();
                break;
            case PlaybackFailureChoice.Remove:
                HandlerBoundary.Run(nameof(RemoveAndCloseAsync), RemoveAndCloseAsync);
                break;
        }
    }

    private async Task RemoveAndCloseAsync()
    {
        await _requestRemove(_channel);
        Close();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_backend is null)
        {
            return; // slider default can raise this during InitializeComponent, before the backend exists
        }

        _backend.Volume = (int)Math.Round(e.NewValue);
        if (_isMuted && e.NewValue > 0)
        {
            _isMuted = false;
            _backend.Mute = false;
            UpdateMuteButton();
        }

        if (_settingsReady)
        {
            // The engine already heard the new level; only the persisted preference waits for the
            // slider to settle. Dragging it back and forth used to queue one whole-catalog save per
            // pixel of travel - hundreds of multi-megabyte writes, each a chance for the state folder
            // to be locked, and the failure took the process down.
            RestartVolumeSaveDelay();
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        _backend.Mute = _isMuted;
        UpdateMuteButton();
        PersistAudioPreferences();
    }

    private void RestartVolumeSaveDelay()
    {
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void VolumeSaveTimer_Tick(object? sender, EventArgs e) => PersistAudioPreferences();

    // Writes the level the slider now holds and drops any pending debounce, so the mute button and the
    // closing window both persist immediately instead of racing a timer. Fire-and-forget on purpose:
    // the owner already reports a failed write, and an `async void` here would kill the process.
    private void PersistAudioPreferences()
    {
        _volumeSaveTimer.Stop();
        _ = _saveAudioPreferences((int)Math.Round(VolumeSlider.Value), _isMuted);
    }

    // SP-0113: the glyph shows the action a click performs, like the caption beside it - media.mute
    // while sound plays, media.volume while muted (ICON-RENDER 4).
    private void UpdateMuteButton()
    {
        MuteButton.Style = (Style)FindResource(_isMuted ? "PlayerOverlayUnmuteGlyphButton" : "PlayerOverlayMuteGlyphButton");
        MuteButton.SetResourceReference(ContentControl.ContentProperty, _isMuted ? "Unmute" : "Mute");
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        // The glyph sits near the window edge. Opening upward aligns the menu to its left edge and
        // leaves it above the native video surface instead of clipping the rightmost labels.
        var menu = new ContextMenu { Placement = PlacementMode.Top };
        var topmost = new MenuItem
        {
            Header = LocalizationService.Get(Topmost ? "PlayerAlwaysOnTopOff" : "PlayerAlwaysOnTopOn")
        };
        topmost.Click += (_, _) => HandlerBoundary.Run("PlayerAlwaysOnTop.Click", () => _saveTopmost(!Topmost));

        var pin = new MenuItem
        {
            Header = LocalizationService.Get(_isPinned() ? "MenuUnpin" : "MenuPin")
        };
        pin.Click += (_, _) => HandlerBoundary.Run("PlayerPin.Click", () => _savePinned(!_isPinned()));

        var about = new MenuItem { Header = LocalizationService.Get("MenuAboutChannel") };
        about.Click += AboutChannel_Click;

        menu.Items.Add(topmost);
        menu.Items.Add(pin);
        menu.Items.Add(about);
        menu.Items.Add(BuildCollectionMenu());
        menu.Items.Add(BuildNewCollectionMenuItem());
        ShowControlPanelMenu(ActionsButton, menu);
    }

    // SP-0053: the engine in this window already holds the stream's description, so nothing is opened.
    private void AboutChannel_Click(object sender, RoutedEventArgs e)
    {
        var collections = _getCollections()
            .Where(collection => collection.ChannelIds.Contains(_channel.Id))
            .Select(collection => collection.Name)
            .ToArray();
        new ChannelInfoWindow(_channel, collections, DescribeTransmission, (tag, fields) => _log.Event(tag, fields)) { Owner = this }.ShowDialog();
    }

    private MenuItem BuildCollectionMenu()
    {
        var parent = new MenuItem { Header = LocalizationService.Get("CollectionMenu") };
        var collections = _getCollections();
        if (collections.Count == 0)
        {
            parent.Items.Add(new MenuItem
            {
                Header = LocalizationService.Get("PlayerCollectionsEmpty"),
                IsEnabled = false
            });
            return parent;
        }

        foreach (var collection in collections)
        {
            var item = new MenuItem
            {
                Header = collection.Name,
                IsCheckable = true,
                IsChecked = collection.ChannelIds.Contains(_channel.Id),
                Tag = collection.Id
            };
            item.Click += (_, _) => HandlerBoundary.Run("PlayerCollectionMembership.Click", async () =>
            {
                if (item.Tag is Guid collectionId)
                {
                    await _saveCollectionMembership(collectionId, item.IsChecked);
                }
            });
            parent.Items.Add(item);
        }

        return parent;
    }

    private MenuItem BuildNewCollectionMenuItem()
    {
        var nameBox = new TextBox
        {
            Width = 130,
            Margin = new Thickness(6, 0, 0, 0),
            MaxLength = ChannelCollections.MaximumNameLength
        };
        nameBox.KeyDown += NewCollectionNameBox_KeyDown;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get("PlayerAddNewCollection"),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(nameBox);
        return new MenuItem { Header = panel, StaysOpenOnClick = true };
    }

    private async void NewCollectionNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.Enter || sender is not TextBox box)
            {
                return;
            }

            e.Handled = true;
            if (!await _createCollection(box.Text))
            {
                MessageBox.Show(
                    this,
                    LocalizationService.Get("CollectionNameInvalid"),
                    LocalizationService.Get("PlayerActions"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(NewCollectionNameBox_KeyDown), exception);
        }
    }

    // Called by MainWindow after a shared player preference changes in another player window.
    internal void ApplyPlayerTopmost(bool topmost) => Topmost = topmost;

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // Any click on the video re-shows the controls and restarts the shared hide countdown.
    // A double click on the video itself toggles fullscreen, the gesture every desktop video
    // player trains; F11 and the overlay button remain the other two ways in and out.
    private void VideoSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ShowControls();
        if (IsOnControlPanel(e.OriginalSource))
        {
            return;
        }

        e.Handled = true;
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ToggleFullscreen();
        }
    }

    // The overlay's preview handler also sees clicks aimed at the control panel, so a double
    // click on a button or on the volume slider must not resize the window.
    private bool IsOnControlPanel(object source) =>
        source is Visual visual && (ReferenceEquals(visual, ControlPanel) || ControlPanel.IsAncestorOf(visual));

    private void ShowControls()
    {
        if (_closing)
        {
            return;
        }

        ControlPanel.Visibility = Visibility.Visible;
        if (_controlsPinned)
        {
            // SP-0191: the panel is pinned - visible is its resting state, and nothing counts down.
            _controlsHideTimer.Stop();
            return;
        }

        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void ControlsHideTimer_Tick(object? sender, EventArgs e)
    {
        _controlsHideTimer.Stop();
        if (_controlsPinned)
        {
            return;
        }

        if (_openControlPanelMenus.Count > 0)
        {
            _controlsHideTimer.Start();
            return;
        }

        ControlPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// SP-0191: applies the keep-visible preference and draws the toggle's state
    /// (<c>ICON-RENDER</code> 3 rule 4). The style swap and the name re-point live in the same block:
    /// the name is the one meaning both states share, and it is carried by resource so a language
    /// change follows it.
    /// </summary>
    internal void ApplyControlsPin(bool pinned)
    {
        _controlsPinned = pinned;
        PinControlsButton.Style = (Style)FindResource(pinned
            ? "PlayerOverlayControlsPinnedGlyphButton"
            : "PlayerOverlayControlsUnpinnedGlyphButton");
        PinControlsButton.SetResourceReference(FrameworkElement.ToolTipProperty, "PlayerPinControlsName");
        PinControlsButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "PlayerPinControlsName");
        if (pinned)
        {
            ShowControls();
        }
        else if (ControlPanel.Visibility == Visibility.Visible)
        {
            _controlsHideTimer.Stop();
            _controlsHideTimer.Start();
        }
    }

    private void PinControlsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saveKeepControlsVisible is { } save)
        {
            HandlerBoundary.Run("PinControlsButton_Click", () => save(!_controlsPinned));
        }
        else
        {
            ApplyControlsPin(!_controlsPinned);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && !e.IsRepeat)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.R && !e.IsRepeat)
        {
            HandlerBoundary.Run("PlayerRecord.Key", ToggleRecordingAsync);
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            ExitFullscreen();
            return;
        }

        _restoredWindowStyle = WindowStyle;
        _restoredResizeMode = ResizeMode;
        _restoredWindowState = WindowState;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        _fullscreen = true;
        FullscreenButton.Style = (Style)FindResource("PlayerOverlayExitFullscreenGlyphButton");
        FullscreenButton.SetResourceReference(ContentControl.ContentProperty, "ExitFullscreen");
        ShowControls();
    }

    private void ExitFullscreen()
    {
        WindowState = WindowState.Normal;
        WindowStyle = _restoredWindowStyle;
        ResizeMode = _restoredResizeMode;
        WindowState = _restoredWindowState;
        _fullscreen = false;
        FullscreenButton.Style = (Style)FindResource("PlayerOverlayFullscreenGlyphButton");
        FullscreenButton.SetResourceReference(ContentControl.ContentProperty, "Fullscreen");
        ShowControls();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PlayerWindow_Closed(object? sender, EventArgs e)
    {
        _closing = true; // stop any pending background reconnect
        _sessionCts.Cancel(); // abort any in-flight recovery backoff so the old stream never restarts
        _wake?.Dispose(); // release the idle-sleep + display hold for this video session
        _wake = null;
        _log.Event("PLAYBACK CLOSE", $"watch_ms={_playbackClock.ElapsedMilliseconds}", $"live={_reachedLive}", $"stalls={_stallCount}", $"url={_channel.Url}");
        // SP-0040 criterion 12: one record that answers "did the player cope with this stream" without
        // reconstructing it from the interleaved per-event lines above.
        _sessionClock.Stop();
        _log.Event("PLAYBACK SESSION",
            $"session_ms={_sessionClock.ElapsedMilliseconds}",
            $"outcome={(_sessionOutcome == "closed" && _firstLiveMs < 0 ? "never_live" : _sessionOutcome)}",
            $"ttff_ms={_firstLiveMs}",
            $"legs={_legCount}",
            $"reconnects={_reconnectCount}",
            $"stalls={_stallCount}",
            $"kind={_channel.MediaKind}",
            $"url={_channel.Url}");
        // A drag that ended with the window being closed still has its level pending; write it now.
        if (_volumeSaveTimer.IsEnabled)
        {
            PersistAudioPreferences();
        }

        _volumeSaveTimer.Tick -= VolumeSaveTimer_Tick;
        _controlsHideTimer.Stop();
        _controlsHideTimer.Tick -= ControlsHideTimer_Tick;
        _statsTimer.Stop();
        _statsTimer.Tick -= StatsTimer_Tick;
        _watchdogTimer.Stop();
        _watchdogTimer.Tick -= WatchdogTimer_Tick;
        DetachBackendEvents(_backend);
        DisposeAttemptResource();

        // The backend tears the native engine down off the UI thread (Stop()/Dispose() block until
        // worker threads settle; on a flapping stream that can take seconds and would freeze the
        // shared WPF UI thread). Its internal gate serializes teardown against any in-flight reconnect.
        EngineReleased = Task.WhenAll(_retiredBackendReleases.Append(_backend.StopAndDisposeAsync()));
        FinishRecordingOnClose(EngineReleased);
    }
}
