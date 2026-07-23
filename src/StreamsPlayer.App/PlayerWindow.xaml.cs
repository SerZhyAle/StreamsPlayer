using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private static readonly TimeSpan FullscreenControlsTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatsSampleInterval = TimeSpan.FromSeconds(2);
    // Part D stall watchdog: poll every 3 s; a freeze is 3 consecutive polls (~9 s) with no position progress.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _controlsHideTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly Stopwatch _playbackClock = new();
    private readonly StreamChannel _channel;
    private readonly CurrentLog _log;
    private readonly Action<string, BitmapSource>? _onThumbnail;
    private bool _thumbnailCaptured;
    // Tags the next snapshot as a user-initiated "save icon" so only that one raises the confirmation toast.
    private bool _manualSnapshotPending;
    private volatile bool _closing;
    private readonly Func<Guid, bool, Task> _recordOutcome;
    private readonly Func<StreamChannel, Task> _requestRemove;
    private readonly Func<bool, Task> _saveTopmost;
    // Pins/unpins this channel in the catalog; the owner (MainWindow) persists and re-filters.
    private readonly Func<bool, Task> _savePinned;
    private bool _pinned;
    private readonly Func<int, bool, Task> _saveAudioPreferences;
    private readonly bool _startFullscreen;
    // SP-0026: the selected video engine (LibVLC by default, FlyleafLib opt-in). The Play/teardown
    // race protection now lives inside the backend; this window drives engine-agnostic orchestration.
    private readonly IVideoBackend _backend;
    private bool _outcomeRecorded;
    private bool _reachedLive;
    private bool _isStalled;
    private int _stallCount;
    // SP-0015 bounded live recovery (policy lives in Core; this window feeds signals and applies decisions).
    private readonly LivePlaybackRecoveryPolicy _recovery = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly DispatcherTimer _watchdogTimer;
    private bool _recovering;        // label guard: a Reconnecting label is showing
    private bool _recoveryInFlight;  // re-entry guard: a decision for the current failure is being applied
    private long _lastWatchdogTime;
    private int _frozenPolls;
    private bool _buffering;
    private long _bufferingSinceMs;
    private long _bufferingStartPosition;
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
        bool pinned,
        Func<bool, Task> savePinned,
        bool topmost,
        Func<bool, Task> saveTopmost,
        int volume,
        bool muted,
        Func<int, bool, Task> saveAudioPreferences,
        Action<string, BitmapSource>? onThumbnail,
        MediaBackend backend,
        bool startFullscreen = false)
    {
        InitializeComponent();
        _channel = channel;
        _log = log;
        _onThumbnail = onThumbnail;
        _recordOutcome = recordOutcome;
        _requestRemove = requestRemove;
        _pinned = pinned;
        _savePinned = savePinned;
        _saveTopmost = saveTopmost;
        _saveAudioPreferences = saveAudioPreferences;
        _startFullscreen = startFullscreen;
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
        _controlsHideTimer = new DispatcherTimer { Interval = FullscreenControlsTimeout };
        _controlsHideTimer.Tick += ControlsHideTimer_Tick;
        _statsTimer = new DispatcherTimer { Interval = StatsSampleInterval };
        _statsTimer.Tick += StatsTimer_Tick;
        _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
        _watchdogTimer.Tick += WatchdogTimer_Tick;
        _backend.BufferingChanged += Backend_BufferingChanged;
        _backend.EncounteredError += Backend_EncounteredError;
        _backend.EndReached += Backend_EndReached;
        _backend.TracksChanged += Backend_TracksChanged;
        _backend.SnapshotReady += Backend_SnapshotReady;
        Topmost = topmost;
        PlayerTopmostCheckBox.IsChecked = topmost;
        UpdatePinButton();
        _settingsReady = true;
        TitleText.Text = StreamTitleFormatter.Display(channel.Title);
        Loaded += PlayerWindow_Loaded;
        Closed += PlayerWindow_Closed;
    }

    private void PlayerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // System + display wake for the video/RTSP session lifetime (Decision 3: the user is watching).
        // Tied to the window rather than LibVLC's thread-affine, flapping play/pause events, so the
        // hold survives bounded reconnects and is released reliably in PlayerWindow_Closed.
        _wake = WakeGuard.Acquire(keepDisplayOn: true);
        StartMedia("initial");
        _statsTimer.Start();
        _watchdogTimer.Start();
        if (_startFullscreen)
        {
            ToggleFullscreen();
        }
    }

    private void StartMedia(string reason)
    {
        if (_closing)
        {
            return; // window is tearing down; do not touch the (soon) disposed player
        }

        _reachedLive = false;
        _isStalled = false;
        _outcomeRecorded = false;
        _frozenPolls = 0;
        _lastWatchdogTime = 0;
        _buffering = false;
        _playbackClock.Restart();
        var cacheMs = reason == "initial" ? LiveCacheMilliseconds : ReconnectCacheMilliseconds;
        _log.Event("PLAYBACK OPEN", $"reason={reason}", $"kind={_channel.MediaKind}", $"cache_ms={cacheMs}", $"url={_channel.Url}");
        if (!_backend.Play(new Uri(_channel.Url), cacheMs, rtspOverTcp: true, softwareDecode: true))
        {
            ShowPlaybackFailure("play_rejected");
        }
    }

    private bool CaptureThumbnail() => _backend.RequestSnapshot(480); // 480 wide, aspect preserved; result via SnapshotReady

    // SP-0024: adopt the frame on screen right now as this channel's grid icon (reuses the snapshot path).
    private void SaveFrameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_reachedLive)
        {
            return; // no frame has rendered yet — stay silent (AC 3)
        }

        _manualSnapshotPending = true;
        if (!CaptureThumbnail())
        {
            _manualSnapshotPending = false; // snapshot rejected (e.g. surface not ready) — no toast
        }
    }

    private void Backend_SnapshotReady(BitmapSource frame)
    {
        // Hand off on the UI thread; the frozen image is safe to encode from a worker later.
        Dispatcher.BeginInvoke(() =>
        {
            _onThumbnail?.Invoke(_channel.Url, frame);
            if (_manualSnapshotPending)
            {
                _manualSnapshotPending = false;
                ShowFrameSavedToast();
            }
        });
    }

    // ~2s over-video confirmation: fade in, hold, fade out. Independent of the auto-hiding control panel
    // and IsHitTestVisible=false, so it stays legible (and unobtrusive) in fullscreen (AC 4).
    private void ShowFrameSavedToast()
    {
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        FrameSavedToast.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void StatsTimer_Tick(object? sender, EventArgs e) => _backend.LogStats("STATS");

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
                WaitText.Text = LocalizationService.Format("BufferingProgress", percentage);
            }

            if (_reachedLive && !_isStalled)
            {
                _isStalled = true;
                _stallCount++;
                _log.Event("PLAYBACK STALL", $"cache={percentage}", $"count={_stallCount}", $"at_ms={_playbackClock.ElapsedMilliseconds}", $"cache_ms={LiveCacheMilliseconds}", $"url={_channel.Url}");
                _backend.LogStats("STALL STATS");
            }

            return;
        }

        _buffering = false;
        _recovering = false; // reached live — clear any Reconnecting label
        WaitText.SetResourceReference(TextBlock.TextProperty, "PlayingLive");
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
            _recovery.NotifyLive(); // sustained live — restore the full recovery budget
            _log.Event("PLAYBACK LIVE", $"ttff_ms={_playbackClock.ElapsedMilliseconds}", $"url={_channel.Url}");
            _ = _recordOutcome(_channel.Id, true);
            if (!_thumbnailCaptured && _onThumbnail is not null)
            {
                _thumbnailCaptured = true;
                _ = CaptureThumbnailSoonAsync();
            }
        }
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

    private static void OpenTrackMenu(
        Button button,
        IReadOnlyList<VideoTrack> tracks,
        int selectedTrackId,
        Action<int> selectTrack)
    {
        var menu = new ContextMenu { PlacementTarget = button };
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

        button.ContextMenu = menu;
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
                return; // window closed while probing — do not touch the UI or restart
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

            WaitText.Text = LocalizationService.Format("ReconnectingAttempt", decision.Attempt, decision.Budget);
            try
            {
                await Task.Delay(decision.Delay, _sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // stop / close / switch cancelled the wait — never restart the old stream
            }

            if (_closing)
            {
                return;
            }

            // Play off the UI thread (the backend serializes play against teardown) so a flapping stream never freezes WPF.
            await Task.Run(() => { if (!_closing) { StartMedia("recover"); } });
        }
        finally
        {
            _recoveryInFlight = false;
        }
    }

    // Part D stall watchdog for silent freezes (no error thrown). Genuine rebuffering (position still advancing,
    // or a short rebuffer) recovers in place (tuning §4); only a stuck stream is torn down and re-prepared.
    private void WatchdogTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || _recoveryInFlight || !_reachedLive)
        {
            return;
        }

        var position = _backend.PositionMs;

        // Freeze A: nominally playing but the position advanced < 500 ms for 3 consecutive polls (~9 s).
        if (_backend.IsPlaying)
        {
            if (position >= 0 && position - _lastWatchdogTime < 500)
            {
                _frozenPolls++;
            }
            else
            {
                _frozenPolls = 0;
            }

            _lastWatchdogTime = position;
            if (_frozenPolls >= 3)
            {
                _frozenPolls = 0;
                _log.Event("PLAYBACK WATCHDOG", "kind=frozen", $"pos_ms={position}", $"url={_channel.Url}");
                _ = RecoverAsync(new PlaybackFailureSignal("stall_frozen", Stall: true));
                return;
            }
        }
        else
        {
            _frozenPolls = 0;
            if (position >= 0)
            {
                _lastWatchdogTime = position;
            }
        }

        // Freeze B: buffering longer than 15 s with no position progress (a stuck buffer, not a live rebuffer).
        if (_buffering)
        {
            var bufferingMs = _playbackClock.ElapsedMilliseconds - _bufferingSinceMs;
            if (bufferingMs > 15_000 && (position < 0 || position - _bufferingStartPosition < 500))
            {
                _log.Event("PLAYBACK WATCHDOG", "kind=stuck_buffer", $"buffering_ms={bufferingMs}", $"url={_channel.Url}");
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

        WaitText.SetResourceReference(TextBlock.TextProperty, "PlayerUnavailable");
        _log.Event("PLAYBACK FAIL", $"reason={reason}", $"at_ms={_playbackClock.ElapsedMilliseconds}", $"kind={_channel.MediaKind}", $"url={_channel.Url}");
        if (!_outcomeRecorded)
        {
            _outcomeRecorded = true;
            _ = _recordOutcome(_channel.Id, false);
        }

        if (notifyUser)
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
        var dialog = new PlaybackFailureDialog(_channel.Title, _channel.SourceOrigin, report) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case PlaybackFailureChoice.Retry:
                _recovery.Reset(); // a manual retry starts a fresh recovery budget
                _recovering = false;
                StartMedia("retry");
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

    private async void PlayerTopmostCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        var topmost = PlayerTopmostCheckBox.IsChecked == true;
        Topmost = topmost;
        await _saveTopmost(topmost);
    }

    private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
            await _saveAudioPreferences((int)Math.Round(e.NewValue), _isMuted);
        }
    }

    private async void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        _backend.Mute = _isMuted;
        UpdateMuteButton();
        await _saveAudioPreferences((int)Math.Round(VolumeSlider.Value), _isMuted);
    }

    private void UpdateMuteButton() =>
        MuteButton.SetResourceReference(ContentControl.ContentProperty, _isMuted ? "Unmute" : "Mute");

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        UpdatePinButton();
        await _savePinned(_pinned);
    }

    // Label reflects the action the click will perform: "Pin" when unpinned, "Unpin" when pinned.
    private void UpdatePinButton() =>
        PinButton.SetResourceReference(ContentControl.ContentProperty, _pinned ? "MenuUnpin" : "MenuPin");

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // Any click on the video re-shows the controls and restarts the fullscreen hide countdown.
    private void VideoSurface_MouseDown(object sender, MouseButtonEventArgs e) => ShowControls();

    private void ShowControls()
    {
        ControlPanel.Visibility = Visibility.Visible;
        _controlsHideTimer.Stop();
        if (_fullscreen)
        {
            _controlsHideTimer.Start();
        }
    }

    private void ControlsHideTimer_Tick(object? sender, EventArgs e)
    {
        _controlsHideTimer.Stop();
        if (_fullscreen)
        {
            ControlPanel.Visibility = Visibility.Collapsed;
        }
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
        _controlsHideTimer.Stop();
        ControlPanel.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PlayerWindow_Closed(object? sender, EventArgs e)
    {
        _closing = true; // stop any pending background reconnect
        _sessionCts.Cancel(); // abort any in-flight recovery backoff so the old stream never restarts
        _wake?.Dispose(); // release the idle-sleep + display hold for this video session
        _wake = null;
        _log.Event("PLAYBACK CLOSE", $"watch_ms={_playbackClock.ElapsedMilliseconds}", $"live={_reachedLive}", $"stalls={_stallCount}", $"url={_channel.Url}");
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
