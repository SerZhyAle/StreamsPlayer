using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using StreamPlayer.Core;

namespace StreamPlayer.App;

public partial class PlayerWindow : Window
{
    private const uint LiveCacheMilliseconds = 10_000;
    private readonly StreamChannel _channel;
    private readonly Func<Guid, bool, Task> _recordOutcome;
    private readonly Func<bool, Task> _saveTopmost;
    private readonly Func<int, bool, Task> _saveAudioPreferences;
    private readonly bool _startFullscreen;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _media;
    private bool _outcomeRecorded;
    private bool _settingsReady;
    private bool _isMuted;
    private bool _fullscreen;
    private WindowStyle _restoredWindowStyle;
    private ResizeMode _restoredResizeMode;
    private WindowState _restoredWindowState;

    public PlayerWindow(
        StreamChannel channel,
        Func<Guid, bool, Task> recordOutcome,
        bool topmost,
        Func<bool, Task> saveTopmost,
        int volume,
        bool muted,
        Func<int, bool, Task> saveAudioPreferences,
        bool startFullscreen = false)
    {
        InitializeComponent();
        _channel = channel;
        _recordOutcome = recordOutcome;
        _saveTopmost = saveTopmost;
        _saveAudioPreferences = saveAudioPreferences;
        _startFullscreen = startFullscreen;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--quiet", "--rtsp-tcp");
        _mediaPlayer = new MediaPlayer(_libVlc) { NetworkCaching = LiveCacheMilliseconds };
        VolumeSlider.Value = Math.Clamp(volume, 0, 100);
        _mediaPlayer.Volume = (int)VolumeSlider.Value;
        _isMuted = muted;
        _mediaPlayer.Mute = muted;
        UpdateMuteButton();
        _mediaPlayer.Buffering += MediaPlayer_Buffering;
        _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
        _mediaPlayer.ESAdded += MediaPlayer_TracksChanged;
        _mediaPlayer.ESSelected += MediaPlayer_TracksChanged;
        Player.MediaPlayer = _mediaPlayer;
        Topmost = topmost;
        PlayerTopmostCheckBox.IsChecked = topmost;
        _settingsReady = true;
        TitleText.Text = StreamTitleFormatter.Display(channel.Title);
        Loaded += PlayerWindow_Loaded;
        Closed += PlayerWindow_Closed;
    }

    private void PlayerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _media = new Media(_libVlc, new Uri(_channel.Url));
        _media.AddOption($":network-caching={LiveCacheMilliseconds}");
        _media.AddOption($":live-caching={LiveCacheMilliseconds}");
        _media.AddOption(":rtsp-tcp");
        if (!_mediaPlayer.Play(_media))
        {
            ShowPlaybackFailure();
        }
        if (_startFullscreen)
        {
            ToggleFullscreen();
        }
    }

    private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e) =>
        Dispatcher.BeginInvoke(() => UpdateBuffering(e.Cache));

    private void UpdateBuffering(float cache)
    {
        var percentage = Math.Clamp((int)Math.Round(cache), 0, 100);
        BufferProgress.Value = percentage;
        if (percentage < 100)
        {
            WaitText.Text = LocalizationService.Format("BufferingProgress", percentage);
            return;
        }

        WaitText.SetResourceReference(TextBlock.TextProperty, "PlayingLive");
        RefreshTrackControls();
        if (!_outcomeRecorded)
        {
            _outcomeRecorded = true;
            _ = _recordOutcome(_channel.Id, true);
        }
    }

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ShowPlaybackFailure);

    private void MediaPlayer_TracksChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshTrackControls);

    private void RefreshTrackControls()
    {
        var audioTracks = _mediaPlayer.AudioTrackDescription?.Where(track => track.Id >= 0).ToArray() ?? [];
        var subtitleTracks = _mediaPlayer.SpuDescription?.Where(track => track.Id >= 0).ToArray() ?? [];
        AudioTracksButton.Visibility = audioTracks.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleTracksButton.Visibility = subtitleTracks.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AudioTracksButton_Click(object sender, RoutedEventArgs e) =>
        OpenTrackMenu(AudioTracksButton, _mediaPlayer.AudioTrackDescription, _mediaPlayer.AudioTrack,
            trackId => _mediaPlayer.SetAudioTrack(trackId));

    private void SubtitleTracksButton_Click(object sender, RoutedEventArgs e) =>
        OpenTrackMenu(SubtitleTracksButton, _mediaPlayer.SpuDescription, _mediaPlayer.Spu,
            trackId => _mediaPlayer.SetSpu(trackId));

    private static void OpenTrackMenu(
        Button button,
        IEnumerable<TrackDescription>? tracks,
        int selectedTrackId,
        Action<int> selectTrack)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var track in tracks?.Where(track => track.Id >= 0) ?? [])
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

    private void ShowPlaybackFailure()
    {
        WaitText.SetResourceReference(TextBlock.TextProperty, "PlayerUnavailable");
        if (!_outcomeRecorded)
        {
            _outcomeRecorded = true;
            _ = _recordOutcome(_channel.Id, false);
        }
        MessageBox.Show(this, LocalizationService.Get("PlayerUnsupported"),
            LocalizationService.Get("StreamUnavailableTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Volume = (int)Math.Round(e.NewValue);
        if (_isMuted && e.NewValue > 0)
        {
            _isMuted = false;
            _mediaPlayer.Mute = false;
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
        _mediaPlayer.Mute = _isMuted;
        UpdateMuteButton();
        await _saveAudioPreferences((int)Math.Round(VolumeSlider.Value), _isMuted);
    }

    private void UpdateMuteButton() =>
        MuteButton.SetResourceReference(ContentControl.ContentProperty, _isMuted ? "Unmute" : "Mute");

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

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
    }

    private void ExitFullscreen()
    {
        WindowState = WindowState.Normal;
        WindowStyle = _restoredWindowStyle;
        ResizeMode = _restoredResizeMode;
        WindowState = _restoredWindowState;
        _fullscreen = false;
        FullscreenButton.SetResourceReference(ContentControl.ContentProperty, "Fullscreen");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PlayerWindow_Closed(object? sender, EventArgs e)
    {
        _mediaPlayer.Buffering -= MediaPlayer_Buffering;
        _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
        _mediaPlayer.ESAdded -= MediaPlayer_TracksChanged;
        _mediaPlayer.ESSelected -= MediaPlayer_TracksChanged;
        _mediaPlayer.Stop();
        Player.MediaPlayer = null;
        _media?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
