using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    public double GridTileWidth => _state.TileSize switch
    {
        StreamTileSize.VerySmall => 120,
        StreamTileSize.Small => 240,
        StreamTileSize.Large => 400,
        _ => 320
    };

    public double GridTileHeight => GridTileWidth * 9 / 16;
    public bool IsVerySmallTile => _state.TileSize == StreamTileSize.VerySmall;

    internal CatalogState State => _state;

    /// <summary>
    /// Deliberately not gated on <c>_preferencesLoaded</c> (SP-0050). The interface language moved in
    /// here from the header, and it has to stay reachable when the catalog load fails: on a failed load
    /// <see cref="PersistAsync"/> discards the write, but <c>LocalizationService.Apply</c> still runs, so
    /// the language changes for the session. That was the header button's behaviour and the ticket's
    /// first constraint requires keeping it.
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSettings();

    internal void OpenSettings(int? initialPage = null)
    {
        try
        {
            var dialog = new SettingsWindow(this, RunToolsActionAsync, () => _tvSchedule, initialPage)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(OpenSettings), exception);
        }
    }

    internal async Task ChangeLanguageFromSettingsAsync(AppLanguage language)
    {
        await PersistAsync(state => state with { Language = language });
        LocalizationService.Apply(language);
        RefreshLocalizedInterface();
    }

    internal async Task ApplyThemeFromSettingsAsync(AppTheme theme)
    {
        await PersistAsync(state => state with { Theme = theme });
        ThemeService.Apply(_state.Theme);
    }

    internal async Task ApplyTileSizeFromSettingsAsync(StreamTileSize tileSize)
    {
        var changed = tileSize != _state.TileSize;
        await PersistAsync(state => state with { TileSize = tileSize });
        if (changed)
        {
            PropertyChanged?.Invoke(this, new(nameof(GridTileWidth)));
            PropertyChanged?.Invoke(this, new(nameof(GridTileHeight)));
            PropertyChanged?.Invoke(this, new(nameof(IsVerySmallTile)));
            _catalogColumns = 0;
            UpdateCatalogColumns();
        }
    }

    internal async Task ApplyHideAdultContentFromSettingsAsync(bool hide)
    {
        var changed = hide != _state.HideAdultContent;
        await PersistAsync(state => state with { HideAdultContent = hide });
        if (changed)
        {
            PopulateFacets();
            ApplyFilter();
        }
    }

    internal async Task ApplyUpdatePreviewsFromSettingsAsync(bool update)
    {
        var changed = update != _state.UpdateStreamPreviews;
        await PersistAsync(state => state with { UpdateStreamPreviews = update });
        if (changed)
        {
            await ApplyPreviewPreferenceAsync();
        }
    }

    internal async Task ApplyAnimatedBackdropFromSettingsAsync(bool animated)
    {
        var changed = animated != _state.AnimatedBackdrop;
        await PersistAsync(state => state with { AnimatedBackdrop = animated });
        if (changed)
        {
            ApplyBackdrop();
        }
    }

    internal async Task ApplyKeepAwakeFromSettingsAsync(bool keepAwake)
    {
        await PersistAsync(state => state with { KeepAwakeDuringPlayback = keepAwake });
        WakeGuard.Enabled = _state.KeepAwakeDuringPlayback;
    }

    internal async Task ApplySystemMediaControlsFromSettingsAsync(bool smtc)
    {
        var changed = smtc != _state.SystemMediaControls;
        await PersistAsync(state => state with { SystemMediaControls = smtc });
        if (changed)
        {
            ApplySystemMediaControlsSetting();
        }
    }

    internal async Task ApplyResumePlaybackFromSettingsAsync(bool resume)
    {
        await PersistAsync(state => state with { ResumePlaybackOnStartup = resume });
    }

    internal async Task ClearResumeHistoryIfDisabledAsync()
    {
        if (!_state.ResumePlaybackOnStartup && _state.ResumeChannelIds.Count > 0)
        {
            await PersistAsync(state => state with { ResumeChannelIds = [] });
        }
    }

    internal async Task ApplyVideoBackendFromSettingsAsync(MediaBackend backend)
    {
        await PersistAsync(state => state with { VideoBackend = backend });
    }

    internal async Task ApplyAudioOutputDeviceFromSettingsAsync(string? deviceId)
    {
        var changed = deviceId != _state.AudioOutputDevice;
        await PersistAsync(state => state with { AudioOutputDevice = deviceId });
        if (changed)
        {
            _standardAudioPlayback.AudioOutputDevice = deviceId;
            foreach (var player in _playerWindows)
            {
                player.ApplyAudioOutputSettings(deviceId, _state.AudioChannelMode);
            }
        }
    }

    internal async Task ApplyAudioChannelModeFromSettingsAsync(AudioChannelMode channelMode)
    {
        var changed = channelMode != _state.AudioChannelMode;
        await PersistAsync(state => state with { AudioChannelMode = channelMode });
        if (changed)
        {
            _standardAudioPlayback.AudioChannelMode = channelMode;
            foreach (var player in _playerWindows)
            {
                player.ApplyAudioOutputSettings(_state.AudioOutputDevice, channelMode);
            }
        }
    }

    internal async Task ApplyCaptureFolderFromSettingsAsync(CaptureKind kind, string? folder)
    {
        await PersistAsync(state => kind switch
        {
            CaptureKind.StreamVideo => state with { VideoRecordingFolder = folder, CaptureFoldersSchema = CaptureFolderChoices.CurrentSchema },
            CaptureKind.StreamAudio => state with { AudioRecordingFolder = folder, CaptureFoldersSchema = CaptureFolderChoices.CurrentSchema },
            _ => state with { FrameFolder = folder, CaptureFoldersSchema = CaptureFolderChoices.CurrentSchema }
        });
    }

    private async Task ApplyPreviewPreferenceAsync()
    {
        // The coordinator keeps running to show stored thumbnails; the toggle only changes whether blanks are captured.
        if (_previewCoordinator is null || !IsGridMode || !_windowActive)
        {
            return;
        }

        await StartPreviewsAsync();
        await QueueVisibleSafelyAsync(force: false);
    }
}
