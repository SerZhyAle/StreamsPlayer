using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // Favourites live in their own anchored section above the main list (SP-0025), presented as a
    // wrapping tile grid in grid view and a sideways-scrolling card strip in list view.
    public CatalogRowCollection<ChannelRow> PinnedRows { get; } = [];

    // SP-0067: the tile-mode view of the same rows, chunked on the main list's column count so pinned
    // and unpinned tiles sit on one grid. It exists because the section virtualizes now, and a
    // virtualizing panel needs uniform items to virtualize - which a chunked row is and a wrapped tile
    // is not. The strip in list mode binds PinnedRows directly and needs no such thing.
    public CatalogRowCollection<CatalogGridRow> PinnedGridRows { get; } = [];

    // List-mode strip height: one card row (MinHeight 84 + card margins) plus a horizontal scrollbar.
    private const double PinnedStripHeight = 118;

    public bool PinnedSectionCollapsed { get; private set; }
    public bool MainSectionCollapsed { get; private set; }

    public bool HasPinned => PinnedRows.Count > 0;
    private bool IsCatalogEmpty => PinnedRows.Count == 0 && Rows.Count == 0;

    public Visibility PinnedHeaderVisibility => HasPinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinnedContentVisibility =>
        HasPinned && !PinnedSectionCollapsed ? Visibility.Visible : Visibility.Collapsed;

    // SP-0067: the section is two lists sharing a cell now, so its visibility splits by view mode.
    // Exactly one of these is ever Visible, and both are Collapsed when the section is.
    public Visibility PinnedGridVisibility =>
        PinnedContentVisibility == Visibility.Visible && IsGridMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinnedStripVisibility =>
        PinnedContentVisibility == Visibility.Visible && !IsGridMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MainHeaderVisibility => IsCatalogEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MainContentVisibility => MainSectionCollapsed ? Visibility.Collapsed : Visibility.Visible;

    private void NotifyPinnedSectionVisibility()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinnedGridVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinnedStripVisibility)));
    }

    private void InitializeSectionState(CatalogState state)
    {
        PinnedSectionCollapsed = state.PinnedSectionCollapsed;
        MainSectionCollapsed = state.MainSectionCollapsed;
        NotifySectionState();
    }

    // Re-evaluates every section-visibility/collapse binding. Called whenever the pinned set or a
    // collapse flag changes so headers, content regions, and chevrons repaint together.
    private void NotifySectionState()
    {
        foreach (var name in SectionStateProperties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static readonly string[] SectionStateProperties =
    [
        nameof(PinnedSectionCollapsed), nameof(MainSectionCollapsed), nameof(HasPinned),
        nameof(PinnedHeaderVisibility), nameof(PinnedContentVisibility),
        nameof(PinnedGridVisibility), nameof(PinnedStripVisibility),
        nameof(MainHeaderVisibility), nameof(MainContentVisibility)
    ];

    private async void PinnedHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (!HasPinned)
        {
            return;
        }

        PinnedSectionCollapsed = !PinnedSectionCollapsed;
        NotifySectionState();
        UpdatePinnedSectionLayout();
        ScheduleVisiblePreviewUpdate();
        if (_preferencesLoaded)
        {
            _state = await PersistAsync(_state with { PinnedSectionCollapsed = PinnedSectionCollapsed });
        }
    }

    private async void MainHeader_Click(object sender, MouseButtonEventArgs e)
    {
        MainSectionCollapsed = !MainSectionCollapsed;
        NotifySectionState();
        UpdatePinnedSectionLayout();
        ScheduleVisiblePreviewUpdate();
        if (_preferencesLoaded)
        {
            _state = await PersistAsync(_state with { MainSectionCollapsed = MainSectionCollapsed });
        }
    }

    private void CatalogArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePinnedSectionLayout();

    // Sizes the pinned region to match the active view. The rules are unchanged by SP-0067: tile view
    // grows with content but caps at roughly half the catalog area (scrolling within itself past that)
    // so it never crowds the main list out, and the cap is lifted when the main section is collapsed;
    // list view is a fixed-height sideways strip. What changed is that the size is applied to whichever
    // of the two lists is showing, rather than to the one ScrollViewer that used to wrap both. The
    // scroll-bar visibilities are declared in the markup now, per list, because each list only ever
    // has one orientation.
    private void UpdatePinnedSectionLayout()
    {
        if (PinnedGridList is null || PinnedStripList is null)
        {
            return;
        }

        var available = CatalogArea?.ActualHeight ?? 0;
        PinnedGridList.MaxHeight = MainSectionCollapsed || available <= 0
            ? double.PositiveInfinity
            : Math.Max(GridTileHeight + 24, available * 0.5);
        PinnedStripList.Height = PinnedStripHeight;
    }
}
