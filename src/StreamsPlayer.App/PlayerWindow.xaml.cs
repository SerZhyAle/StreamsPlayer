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
    private static readonly TimeSpan ControlsHideTimeout = TimeSpan.FromSeconds(10);
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
    // SP-0038: read per save, not captured at open, so a folder changed in Settings applies to the next
    // capture of a player window that is already on screen.
    private readonly Func<string?> _frameFolder;
    private bool _thumbnailCaptured;
    // Tags the next snapshot as the user-initiated capture, so only that one writes a file and reports.
    private bool _manualSnapshotPending;
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
    private readonly Func<IReadOnlyList<ChannelCollection>> _getCollections;
    private readonly Func<Guid, bool, Task> _saveCollectionMembership;
    private readonly Func<string, Task<bool>> _createCollection;
    private readonly Func<int, bool, Task> _saveAudioPreferences;
    private readonly bool _startFullscreen;
    // SP-0026: the selected video engine (LibVLC by default, FlyleafLib opt-in). The Play/teardown
    // race protection now lives inside the backend; this window drives engine-agnostic orchestration.
    private readonly IVideoBackend _backend;
    private bool _outcomeRecorded;
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
    // Written by StartMedia, which two of its three callers run on a worker thread, and read on the UI
    // thread by ObserveRendition; volatile so a rendition belonging to the new leg is never filed under
    // the previous one - that mislabelling is precisely what SP-0077's criterion 2 forbids. Incremented
    // in one place only, and StartMedia never runs against itself (_recoveryInFlight guards the paths
    // that could), so a read-modify-write on it is not a race in practice.
    private volatile int _legCount;
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
    private bool _buffering;
    private long _bufferingSinceMs;
    private long _bufferingStartPosition;
    // The live buffer the media currently open was started with - the delay the live caption reports.
    // Written by StartMedia, which two of its three callers run on a worker thread, and read on the UI
    // thread in ShowLiveStatus; volatile so the reader never sees the previous leg's buffer.
    private volatile uint _liveCacheMs = LiveCacheMilliseconds;
    private bool _settingsReady;
    private bool _isMuted;
    private bool _fullscreen;
    private WindowStyle _restoredWindowStyle;
    private ResizeMode _restoredResizeMode;
    private WindowState _restoredWindowState;
    private IDisposable? _wake;

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
        Func<string?> frameFolder,
        MediaBackend backend,
        bool startFullscreen = false,
        bool quietUntilLive = false)
    {
        InitializeComponent();
        _channel = channel;
        _log = log;
        _onThumbnail = onThumbnail;
        _frameFolder = frameFolder;
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
        _backend = VideoBackendFactory.Create(backend, volume, muted, log);
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
        _statsTimer = new DispatcherTimer { Interval = StatsSampleInterval };
        _statsTimer.Tick += StatsTimer_Tick;
        _volumeSaveTimer = new DispatcherTimer { Interval = VolumeSaveDelay };
        _volumeSaveTimer.Tick += VolumeSaveTimer_Tick;
        _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
        _watchdogTimer.Tick += WatchdogTimer_Tick;
        _backend.BufferingChanged += Backend_BufferingChanged;
        _backend.EncounteredError += Backend_EncounteredError;
        _backend.EndReached += Backend_EndReached;
        _backend.TracksChanged += Backend_TracksChanged;
        _backend.SnapshotReady += Backend_SnapshotReady;
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

        StartMedia("initial", QualityCeiling); // SP-0071: the ladder's answer is not in yet - memory's may be
        _statsTimer.Start();
        _watchdogTimer.Start();
        ShowControls();
        if (_startFullscreen)
        {
            ToggleFullscreen();
        }
    }

    /// <summary>
    /// Opens the channel. <paramref name="qualityCeiling"/> is passed in rather than read here because
    /// two of the three callers run this on a worker thread and the governor that owns the answer is
    /// single-threaded; the caller reads it on the UI thread, as late as it can (SP-0071).
    /// </summary>
    private void StartMedia(string reason, StreamQualityRung? qualityCeiling)
    {
        if (_closing)
        {
            return; // window is tearing down; do not touch the (soon) disposed player
        }

        _reachedLive = false;
        _isStalled = false;
        _outcomeRecorded = false;
        // SP-0070: same reason as the health baseline below - the new media restarts the engine's
        // progress counters from zero, and differencing across that boundary would invent a freeze.
        _freeze.Reset();
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
        if (!_backend.Play(new Uri(_channel.Url), cacheMs, rtspOverTcp: true, softwareDecode: true, qualityCeiling))
        {
            ShowPlaybackFailure("play_rejected");
        }
    }

    private const int IconWidth = 480;

    /// <summary>SP-0053: the channel this window is playing, so the About window can be offered for it.</summary>
    internal StreamChannel Channel => _channel;

    /// <summary>SP-0053: this window's engine already has the description; nothing is opened for it.</summary>
    internal StreamTransmission? DescribeTransmission() => _backend.DescribeTransmission();

    private bool CaptureThumbnail() => _backend.RequestSnapshot(IconWidth); // aspect preserved; result via SnapshotReady

    // SP-0038: one press, two effects - the frame on screen is written to a picture file the user owns
    // and adopted as this channel's grid icon (SP-0024). Zero asks both backends for the stream's own
    // resolution: the file is the point of the feature, and the icon is downscaled from the same frame.
    private void SaveFrameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_reachedLive)
        {
            return; // no frame has rendered yet - stay silent (AC 3)
        }

        _manualSnapshotPending = true;
        if (!_backend.RequestSnapshot(0))
        {
            _manualSnapshotPending = false; // snapshot rejected (e.g. surface not ready) - no toast
        }
    }

    private void Backend_SnapshotReady(BitmapSource frame)
    {
        // Hand off on the UI thread; freezing here is what makes the image safe to encode from a worker.
        Dispatcher.BeginInvoke(() =>
        {
            frame = Frozen(frame);
            if (!_manualSnapshotPending)
            {
                _onThumbnail?.Invoke(_channel.Url, frame);
                return;
            }

            _manualSnapshotPending = false;
            _onThumbnail?.Invoke(_channel.Url, ToIconSize(frame));
            _ = SaveFrameFileAsync(frame);
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
            var path = await CapturedFrameWriter.SaveAsync(
                frame, _frameFolder(), StreamTitleFormatter.Display(_channel.Title), DateTimeOffset.Now);
            _log.Event("FRAME SAVE", "ok=true", $"size={frame.PixelWidth}x{frame.PixelHeight}", $"path={path}");
            ShowFrameToast(LocalizationService.Format("FrameSaved", Path.GetFileName(path)));
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
    private void ShowFrameToast(string message)
    {
        FrameToastText.Text = message;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        FrameSavedToast.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // SP-0045 rides this existing tick rather than adding one: the observation cadence is the budget.
    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        _backend.LogStats("STATS");
        SampleSignalHealth();
        ObserveQuality(); // SP-0071 rides this tick too: the probe clock, no timer of its own
        ObserveRendition(); // SP-0077 rides it as well: what the engine actually put on screen
        ApplyInterruptionNotice(); // SP-0072 rides it as well: what makes the appear delay elapse
        SampleNowPlaying(); // SP-0073 rides it too: the stream's own "what is on air", no poll of its own
    }

    // Backend raises EndReached on its own thread; hop to the UI thread before driving recovery.
    private void Backend_EndReached()
    {
        // A live stream reporting EndReached has usually just dropped; route it through the bounded recovery
        // policy (re-opening a live HLS stream naturally re-anchors to the live edge). Cancellable via _sessionCts.
        Dispatcher.BeginInvoke(() => _ = RecoverAsync(new PlaybackFailureSignal("end_reached", EndReached: true)));
    }

    private void Backend_BufferingChanged(float cache) =>
        Dispatcher.BeginInvoke(() => UpdateBuffering(cache));

    private void UpdateBuffering(float cache)
    {
        var percentage = Math.Clamp((int)Math.Round(cache), 0, 100);
        BufferProgress.Value = percentage;
        if (percentage < 100)
        {
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

        _buffering = false;
        _recovering = false; // reached live - clear any Reconnecting label
        ShowLiveStatus();
        // SP-0072: the repo's own definition of "the picture is back" - this is where PLAYBACK LIVE is
        // logged, and the ticket's 3-18 s blackouts were measured to that line.
        NotifyPictureLive();
        RefreshTrackControls();
        if (_isStalled)
        {
            _isStalled = false;
            _log.Event("PLAYBACK RESUME", $"count={_stallCount}", $"at_ms={_playbackClock.ElapsedMilliseconds}", $"url={_channel.Url}");
            _backend.LogStats("RESUME STATS");
        }

        if (!_outcomeRecorded)
        {
            _outcomeRecorded = true;
            _reachedLive = true;
            _quietUntilLive = false; // SP-0062: from here on this is an ordinary window
            _recovery.NotifyLive(); // sustained live - restore the full recovery budget
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
            _ = _recordOutcome(_channel.Id, true);
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

    private void Backend_EncounteredError() =>
        Dispatcher.BeginInvoke(() => _ = RecoverAsync(new PlaybackFailureSignal("encountered_error")));

    private void Backend_TracksChanged() =>
        Dispatcher.BeginInvoke(RefreshTrackControls);

    private void RefreshTrackControls()
    {
        AudioTracksButton.Visibility = _backend.AudioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleTracksButton.Visibility = _backend.SubtitleTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
            // Only a fresh http/https open failure needs the status probe; stall/end/live-window already carry their signal.
            var enriched = signal;
            if (signal.HttpStatusCode is null && !signal.Stall && !signal.EndReached && !signal.BehindLiveWindow)
            {
                enriched = signal with { HttpStatusCode = await PlaybackStatusProbe.TryGetStatusAsync(_channel.Url, _sessionCts.Token) };
            }

            if (_closing)
            {
                return; // window closed while probing - do not touch the UI or restart
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
            await Task.Run(() => { if (!_closing) { StartMedia("recover", ceiling); } });
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

        // Freeze A: nothing is reaching the screen and nothing is arriving from the source (SP-0070).
        // The rule is in Core; this tick is only its observation cadence, and the threshold is a duration
        // there rather than a poll count here.
        if (_freeze.Observe(HealthNow, _backend.IsPlaying, position, _backend.ReadProgressCounters()))
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

    private void ShowPlaybackFailure(string reason, bool notifyUser = true)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowPlaybackFailure(reason, notifyUser));
            return;
        }

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
        if (!_outcomeRecorded)
        {
            _outcomeRecorded = true;
            _ = _recordOutcome(_channel.Id, false);
        }

        if (notifyUser && !_quietUntilLive)
        {
            ShowFailureDialog(reason);
        }
    }

    private void ShowFailureDialog(string reason)
    {
        var report = FailureReportFormatter.Format(new FailureReport(
            ProductInfo.Version,
            DateTimeOffset.UtcNow,
            _channel.Title,
            _channel.Url,
            _channel.MediaKind,
            PlaybackErrorClassifier.Classify(reason)));
        var dialog = new PlaybackFailureDialog(_channel.Title, _channel.SourceOrigin, report, _channel.Access) { Owner = this };
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
                StartMedia("retry", QualityCeiling);
                break;
            case PlaybackFailureChoice.Remove:
                _ = RemoveAndCloseAsync();
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

    private void UpdateMuteButton() =>
        MuteButton.SetResourceReference(ContentControl.ContentProperty, _isMuted ? "Unmute" : "Mute");

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        // The glyph sits near the window edge. Opening upward aligns the menu to its left edge and
        // leaves it above the native video surface instead of clipping the rightmost labels.
        var menu = new ContextMenu { Placement = PlacementMode.Top };
        var topmost = new MenuItem
        {
            Header = LocalizationService.Get(Topmost ? "PlayerAlwaysOnTopOff" : "PlayerAlwaysOnTopOn")
        };
        topmost.Click += async (_, _) => await _saveTopmost(!Topmost);

        var pin = new MenuItem
        {
            Header = LocalizationService.Get(_isPinned() ? "MenuUnpin" : "MenuPin")
        };
        pin.Click += async (_, _) => await _savePinned(!_isPinned());

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
        new ChannelInfoWindow(_channel, collections, DescribeTransmission) { Owner = this }.ShowDialog();
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
            item.Click += async (_, _) =>
            {
                if (item.Tag is Guid collectionId)
                {
                    await _saveCollectionMembership(collectionId, item.IsChecked);
                }
            };
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
        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void ControlsHideTimer_Tick(object? sender, EventArgs e)
    {
        _controlsHideTimer.Stop();
        if (_openControlPanelMenus.Count > 0)
        {
            _controlsHideTimer.Start();
            return;
        }

        ControlPanel.Visibility = Visibility.Collapsed;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fullscreen)
        {
            ExitFullscreen();
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
        _backend.BufferingChanged -= Backend_BufferingChanged;
        _backend.EncounteredError -= Backend_EncounteredError;
        _backend.EndReached -= Backend_EndReached;
        _backend.TracksChanged -= Backend_TracksChanged;
        _backend.SnapshotReady -= Backend_SnapshotReady;

        // The backend tears the native engine down off the UI thread (Stop()/Dispose() block until
        // worker threads settle; on a flapping stream that can take seconds and would freeze the
        // shared WPF UI thread). Its internal gate serializes teardown against any in-flight reconnect.
        _ = _backend.StopAndDisposeAsync();
    }
}
