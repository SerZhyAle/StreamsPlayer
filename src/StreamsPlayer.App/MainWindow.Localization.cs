using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private bool _preferencesLoaded;
    private bool _updatingLocalizedOptions;

    // Null means the status line is deliberately empty (ClearStatus), not that it was never set.
    private string? _statusResourceKey = "Ready";
    private object?[] _statusArguments = [];
    private string _nowPlayingResourceKey = "NothingPlaying";
    private object?[] _nowPlayingArguments = [];

    /// <summary>
    /// The only way this window writes state.
    /// <para>
    /// Persistence begins only after startup has either loaded state or deliberately chosen a fresh one
    /// after a load failure. That keeps event handlers from writing while initialization is incomplete
    /// without turning a recoverable failed load into a silent one. After a failed load the store itself
    /// refuses every save (SP-0175), so the unread file survives to the restart the notice asks for;
    /// the refusals surface as ordinary failed saves.
    /// </para>
    /// </summary>
    private CatalogStateCommitter CreateStateCommitter(CatalogState initialState) =>
        new(initialState, (state, cancellationToken) => _store.SaveAsync(state, cancellationToken: cancellationToken),
            mutation => AssertUiIndependentMutation(mutation));

    private Task PersistAsync(Func<CatalogState, CatalogState> mutation)
        => PersistAsync(mutation, (state, cancellationToken) => _store.SaveAsync(state, cancellationToken: cancellationToken));

    /// <summary>Commits one mutation with an associated atlas write, still under the window's one state gate.</summary>
    private async Task PersistAsync(
        Func<CatalogState, CatalogState> mutation,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save)
    {
        AssertUiIndependentMutation(mutation);
        await PersistAsync((state, _) => Task.FromResult(mutation(state)), save);
    }

    private async Task PersistAsync(
        Func<CatalogState, CancellationToken, Task<CatalogState>> mutation,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save,
        CancellationToken cancellationToken = default)
        => await CommitStateAsync(mutation, save, cancellationToken);

    private Task<CatalogStateCommitResult> CommitStateAsync(
        Func<CatalogState, CatalogState> mutation,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save)
    {
        AssertUiIndependentMutation(mutation);
        return CommitStateAsync((state, _) => Task.FromResult(mutation(state)), save);
    }

    private async Task<CatalogStateCommitResult> CommitStateAsync(
        Func<CatalogState, CancellationToken, Task<CatalogState>> mutation,
        Func<CatalogState, CancellationToken, Task<CatalogState>> save,
        CancellationToken cancellationToken = default)
    {
        AssertUiIndependentMutation(mutation);
        if (!_preferencesLoaded || _stateCommitter is null)
        {
            return new CatalogStateCommitResult(_state, Saved: false,
                Failure: new InvalidOperationException("Catalog state has not loaded."), AttemptedState: _state);
        }

        var result = await _stateCommitter.CommitAsync(mutation, save, cancellationToken);
        _state = _stateCommitter.Current;
        if (!result.Saved && result.Failure is not null)
        {
            _log.Error("Catalog state save failed", result.Failure);
            // The calling action normally sets its success status after this await. Queue the failure
            // after that continuation so the visible status reflects the unsuccessful write.
            _ = Dispatcher.BeginInvoke(() => SetStatus("StateSaveFailed"));
        }

        return result;
    }

    [Conditional("DEBUG")]
    private static void AssertUiIndependentMutation(Delegate mutation)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Check(mutation);

        void Check(object? value)
        {
            if (value is null || !seen.Add(value))
            {
                return;
            }

            Debug.Assert(value is not DispatcherObject,
                "A catalog mutation captured a WPF object; capture its values before queuing the commit.");
            var target = value is Delegate callback ? callback.Target : value;
            Debug.Assert(target is not DispatcherObject,
                "A catalog mutation targets a WPF object; capture its values before queuing the commit.");
            if (target is null || target is DispatcherObject)
            {
                return;
            }

            // Only compiler closure objects and delegates need descent. Domain records may contain
            // arbitrary data; walking them would turn this boundary check into a graph traversal.
            if (target is not Delegate && !target.GetType().Name.Contains("DisplayClass", StringComparison.Ordinal))
            {
                return;
            }

            foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var captured = field.GetValue(target);
                if (captured is DispatcherObject or Delegate ||
                    captured?.GetType().Name.Contains("DisplayClass", StringComparison.Ordinal) == true)
                {
                    Check(captured);
                }
            }
        }
    }

    private async Task SetPlayerTopmostAsync(bool topmost)
    {
        foreach (var window in _playerWindows)
        {
            window.ApplyPlayerTopmost(topmost);
        }

        if ((_stateCommitter?.Requested ?? _state).PlayerWindowTopmost != topmost)
        {
            await PersistAsync(state => state.PlayerWindowTopmost == topmost
                ? state : state with { PlayerWindowTopmost = topmost });
            if (_state.PlayerWindowTopmost != topmost)
            {
                foreach (var window in _playerWindows)
                {
                    window.ApplyPlayerTopmost(_state.PlayerWindowTopmost);
                }
            }
        }
    }

    private async Task SaveVideoAudioPreferencesAsync(int volume, bool muted)
    {
        var requested = _stateCommitter?.Requested ?? _state;
        if (requested.VideoVolume == volume && requested.VideoMuted == muted)
        {
            return;
        }

        await PersistAsync(state => state.VideoVolume == volume && state.VideoMuted == muted
            ? state : state with { VideoVolume = volume, VideoMuted = muted });
    }

    private void RefreshLocalizedInterface()
    {
        // Most of the header needs no update here: content, tooltips and automation names are all
        // DynamicResource bindings and follow the dictionary swap on their own (SP-0034). The operations
        // menu is rebuilt on every open, so it picks the new language up for free.
        UpdateLocalizedOptions();
        // The collection list carries a localized "All" entry, so it is rebuilt with the rest (SP-0017).
        PopulateCollectionFilter();
        PopulateFacets();
        foreach (var row in _rowCache.Values)
        {
            row.RefreshLocalization();
        }

        // Player windows are non-modal, so one can be open while the language changes. Its title and
        // its formatted wait label have no DynamicResource to follow and must be re-rendered here.
        foreach (var player in Application.Current.Windows.OfType<PlayerWindow>())
        {
            player.RefreshLocalization();
        }

        ApplyFilter();
        // The reveal button's tooltip is set in code, not bound, so it has to be re-rendered by hand.
        UpdateFilterPanelChrome();
        RefreshLocalizedStateText();
    }

    private void UpdateLocalizedOptions()
    {
        var selectedMedia = SelectedOptionValue(MediaFilter) ?? AllValue;
        var selectedSort = SelectedOptionValue(SortMode) ?? "Name";
        var selectedMinBitrate = SelectedOptionValue(MinBitrateFilter) ?? AllValue;
        _updatingLocalizedOptions = true;
        try
        {
            var mediaItems = new[]
            {
                new UiOption(AllValue, LocalizationService.Get("AllOption")),
                new UiOption(AudioFilterValue, LocalizationService.Get("AudioOption")),
                new UiOption(VideoFilterValue, LocalizationService.Get("VideoOption")),
                new UiOption(OwnFilterValue, LocalizationService.Get("OwnOption"))
            };
            var minBitrateItems = new[] { new UiOption(AllValue, LocalizationService.Get("AllOption")) }
                .Concat(new[] { 64, 128, 192, 256, 320 }
                    .Select(kbps => new UiOption(
                        kbps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        LocalizationService.Format("BitrateValue", kbps))))
                .ToArray();
            var sortItems = new[]
            {
                new UiOption("Name", LocalizationService.Get("SortName")),
                new UiOption("Topic", LocalizationService.Get("SortTopic")),
                new UiOption("Language", LocalizationService.Get("SortLanguage")),
                new UiOption("Country", LocalizationService.Get("SortCountry")),
                new UiOption("Recently added", LocalizationService.Get("SortRecentlyAdded"))
            };
            MediaFilter.ItemsSource = mediaItems;
            MediaFilter.SelectedItem = mediaItems.First(item => item.Value == selectedMedia);
            SortMode.ItemsSource = sortItems;
            SortMode.SelectedItem = sortItems.First(item => item.Value == selectedSort);
            MinBitrateFilter.ItemsSource = minBitrateItems;
            MinBitrateFilter.SelectedItem = minBitrateItems.FirstOrDefault(item => item.Value == selectedMinBitrate)
                ?? minBitrateItems[0];
        }
        finally
        {
            _updatingLocalizedOptions = false;
        }
    }

    private static string? SelectedOptionValue(ComboBox comboBox) =>
        (comboBox.SelectedItem as UiOption)?.Value;

    private void SetStatus(string resourceKey, params object?[] arguments)
    {
        _statusResourceKey = resourceKey;
        _statusArguments = arguments;
        StatusText.Text = LocalizationService.Format(resourceKey, arguments);
        UpdateCompactPanel();
    }

    private void ClearStatus()
    {
        _statusResourceKey = null;
        _statusArguments = [];
        StatusText.Text = string.Empty;
        UpdateCompactPanel();
    }

    private void SetNowPlaying(string resourceKey, params object?[] arguments)
    {
        _nowPlayingResourceKey = resourceKey;
        _nowPlayingArguments = arguments;
        NowPlayingText.Text = LocalizationService.Format(resourceKey, arguments);
        RefreshWindowTitle();
        UpdateCompactPanel();
    }

    // The title bar (and therefore the taskbar button) names the station currently playing or paused,
    // so a minimised window still says what is on air. Every playback transition goes through
    // SetNowPlaying, so that is the single hook; the product name alone is the idle title.
    private void RefreshWindowTitle()
    {
        var product = LocalizationService.Get("ProductName");
        var station = _playingAudio?.DisplayTitle
            ?? (PausedAudioChannel is { } paused ? StreamTitleFormatter.Display(paused.Title) : null);
        Title = string.IsNullOrWhiteSpace(station)
            ? product
            : LocalizationService.Format("WindowTitleWithSubject", station, product);
    }

    private void RefreshLocalizedStateText()
    {
        StatusText.Text = _statusResourceKey is null
            ? string.Empty
            : LocalizationService.Format(_statusResourceKey, _statusArguments);
        NowPlayingText.Text = LocalizationService.Format(_nowPlayingResourceKey, _nowPlayingArguments);
        RefreshWindowTitle();
        // SP-0080: the panel is a copy of these two lines, so a language change reaches it here and
        // nowhere else. Its own captions are DynamicResource and follow the switch on their own.
        UpdateCompactPanel();
    }
}
