using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private void RestoreBrowsingSession()
    {
        _restoringBrowsingSession = true;
        try
        {
            SearchBox.Text = _session.SearchQuery;
            SelectOptionValue(MediaFilter, _session.MediaFilter, AllValue);
            SelectOptionValue(CategoryFilter, _session.CategoryFilter, AllValue);
            SelectOptionValue(TopicFilter, _session.TopicFilter, AllValue);
            SelectOptionValue(LanguageFilter, _session.LanguageFilter, AllValue);
            SelectOptionValue(CountryFilter, _session.CountryFilter, AllValue);
            SelectOptionValue(MinBitrateFilter, _session.MinBitrateFilter, AllValue);
            SelectOptionValue(CollectionFilter, _session.CollectionFilter, AllValue);
            SelectOptionValue(SortMode, _session.SortMode, "Name");
            _lastScrollOffset = _session.ScrollOffset;
        }
        finally
        {
            _restoringBrowsingSession = false;
        }
    }

    private static void SelectOptionValue(ComboBox comboBox, string? value, string fallback)
    {
        var selected = value ?? fallback;
        comboBox.SelectedItem = comboBox.Items.OfType<UiOption>().FirstOrDefault(item =>
            item.Value.Equals(selected, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<UiOption>().FirstOrDefault(item => item.Value == fallback);
    }

    /// <summary>
    /// The list's own scrolling surface, found once and kept (SP-0067).
    /// </summary>
    /// <remarks>
    /// Looked up through the visual tree rather than by template part name: a <see cref="ListView"/>'s
    /// default template is a theme resource and its part names are not a contract. Cached because the
    /// alternative is a tree walk on the scroll path, which is the class of cost this ticket removes.
    /// </remarks>
    private ScrollViewer? StreamsScroll => _streamsScroll ??= FindScrollViewer(StreamsList);

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Puts the list back where the user left it, to the pixel the session recorded.
    /// </summary>
    /// <remarks>
    /// The restore is deliberately approximate across a filter or width change (SP-0067 settled
    /// question 1): the session stores a position, not a channel identity, so a different filter or a
    /// different column count lands in the same neighbourhood rather than on the same top channel. That
    /// is the accepted trade for a scroll handler that costs one number instead of a walk over every
    /// row - do not "fix" it back to an anchor that names a channel.
    /// </remarks>
    private async Task RestoreScrollAnchorAsync()
    {
        if (_lastScrollOffset <= 0)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        StreamsScroll?.ScrollToVerticalOffset(_lastScrollOffset);
    }

    private void ScrollToCatalogStart()
    {
        _lastScrollOffset = 0;
        StreamsScroll?.ScrollToTop();
    }

    private void ScheduleBrowsingSessionSave()
    {
        if (!_preferencesLoaded || _restoringBrowsingSession)
        {
            return;
        }

        _browsingSessionSaveTimer.Stop();
        _browsingSessionSaveTimer.Start();
    }

    private async void BrowsingSessionSaveTimer_Tick(object? sender, EventArgs e)
    {
        _browsingSessionSaveTimer.Stop();
        await SaveBrowsingSessionAsync();
    }

    private async Task SaveBrowsingSessionAsync()
    {
        if (!_preferencesLoaded)
        {
            return;
        }

        var started = BeginCatalogPerf();
        var updated = _session with
        {
            SearchQuery = SearchBox.Text,
            MediaFilter = SelectedOptionValue(MediaFilter) ?? AllValue,
            CategoryFilter = SelectedOptionValue(CategoryFilter) ?? AllValue,
            TopicFilter = SelectedOptionValue(TopicFilter) ?? AllValue,
            LanguageFilter = SelectedOptionValue(LanguageFilter) ?? AllValue,
            CountryFilter = SelectedOptionValue(CountryFilter) ?? AllValue,
            MinBitrateFilter = SelectedOptionValue(MinBitrateFilter) ?? AllValue,
            CollectionFilter = SelectedOptionValue(CollectionFilter) ?? AllValue,
            SortMode = SelectedOptionValue(SortMode) ?? "Name",
            ScrollOffset = _lastScrollOffset
        };

        // Still worth checking, for a different reason than before SP-0067. It used to save a 15 MB
        // catalog serialization; now it saves a few hundred bytes, and what it really avoids is the
        // file churn of rewriting an unchanged session on every scroll that comes to rest at the same
        // place. This is now a genuine value comparison over eleven short fields.
        if (updated == _session)
        {
            CatalogPerf("SaveBrowsingSessionAsync", started, "wrote=false", "bytes=0");
            return;
        }

        _session = updated;
        await PersistSessionAsync();
        CatalogPerf("SaveBrowsingSessionAsync", started, "wrote=true", $"bytes={SessionFileBytes()}");
    }

    /// <summary>
    /// The one way this window writes the browsing session - the counterpart of <c>PersistAsync</c>,
    /// with the same rule that a failed local write must not take the window down with it.
    /// </summary>
    private async Task PersistSessionAsync()
    {
        if (!_preferencesLoaded)
        {
            return;
        }

        try
        {
            await _sessionStore.SaveAsync(_session);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Every caller is an async void handler or a close path. A locked or full state folder is an
            // environment failure; losing a scroll position is the honest cost of it, and cheaper than
            // the unhandled exception that would otherwise close the window.
            _log.Event("SESSION SAVE", "ok=false", $"err={exception.GetType().Name}");
        }
    }

    // Best-effort: the size is diagnostic, and a missing or locked file must not fail a save that
    // already succeeded.
    private long SessionFileBytes()
    {
        try
        {
            return new FileInfo(_sessionStore.SessionPath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>
    /// The size of the channel catalog on disk, reported beside a session save so SP-0067's criterion 3
    /// - "a click writes no catalog" - is checkable from the log rather than from a claim.
    /// </summary>
    private long StateFileBytes()
    {
        try
        {
            return new FileInfo(_store.StatePath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
