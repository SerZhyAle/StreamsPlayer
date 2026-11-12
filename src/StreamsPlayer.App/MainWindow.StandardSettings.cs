using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    /// <summary>
    /// SP-0260 / <c>DEVICE-EXCHANGE</c> item ER: applies the chosen groups of a STANDARD read from the server, once. The
    /// values land in the local state like the person's own choices and act at once; nothing is remembered about the
    /// server, so every later change is local only. Returns true when the interface language changed, so the caller
    /// re-words its own window.
    /// </summary>
    internal async Task<bool> ApplyStandardSettingsAsync(StandardSettingsDocument document, IReadOnlyCollection<string> groups)
    {
        var before = _stateCommitter?.Requested ?? _state;
        var target = StandardSettings.Apply(before, document, groups).State;

        // The three values whose own setters apply them to open windows go through those setters below; everything
        // else is one write of the state.
        await PersistAsync(state => StandardSettings.Apply(state, document, groups).State with
        {
            ViewMode = state.ViewMode,
            PlayerWindowTopmost = state.PlayerWindowTopmost,
            KeepPlayerControlsVisible = state.KeepPlayerControlsVisible
        });

        var after = _stateCommitter?.Requested ?? _state;
        if (after.Theme != before.Theme)
        {
            ThemeService.Apply(after.Theme);
        }

        var languageChanged = after.Language is { } language && language != LocalizationService.CurrentLanguage;
        if (languageChanged)
        {
            LocalizationService.Apply(after.Language!.Value);
            RefreshLocalizedInterface();
        }

        if (after.TileSize != before.TileSize)
        {
            OnTileSizeChanged();
        }

        if (after.AnimatedBackdrop != before.AnimatedBackdrop)
        {
            ApplyBackdrop();
        }

        if (after.HideAdultContent != before.HideAdultContent)
        {
            PopulateFacets();
            ApplyFilter();
        }

        Topmost = after.MainWindowTopmost;
        WakeGuard.Enabled = after.KeepAwakeDuringPlayback;
        if (after.SystemMediaControls != before.SystemMediaControls)
        {
            ApplySystemMediaControlsSetting();
        }

        if (after.AudioChannelMode != before.AudioChannelMode)
        {
            _standardAudioPlayback.AudioChannelMode = after.AudioChannelMode;
            if (_fastMediaSorterAudioPlayback is { } broadcast)
            {
                broadcast.AudioChannelMode = after.AudioChannelMode;
            }

            foreach (var player in _playerWindows)
            {
                player.ApplyAudioOutputSettings(after.AudioOutputDevice, after.AudioChannelMode);
            }
        }

        if (after.UpdateStreamPreviews != before.UpdateStreamPreviews)
        {
            await ApplyPreviewPreferenceAsync();
        }

        await SetViewModeAsync(target.ViewMode);
        await SetPlayerTopmostAsync(target.PlayerWindowTopmost);
        await SetKeepPlayerControlsVisibleAsync(target.KeepPlayerControlsVisible);
        return languageChanged;
    }
}
