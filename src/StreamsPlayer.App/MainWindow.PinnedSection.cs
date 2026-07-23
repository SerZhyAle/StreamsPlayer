using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // Favourites live in their own anchored section above the main list (SP-0025). The section is a
    // flat (non-virtualized) collection — favourites are few — presented as a wrapping tile grid in
    // grid view and a sideways-scrolling card strip in list view. The main list keeps virtualization.
    public ObservableCollection<ChannelRow> PinnedRows { get; } = [];

    // List-mode strip height: one card row (MinHeight 84 + card margins) plus a horizontal scrollbar.
    private const double PinnedStripHeight = 118;

    public bool PinnedSectionCollapsed { get; private set; }
    public bool MainSectionCollapsed { get; private set; }

    public bool HasPinned => PinnedRows.Count > 0;
    private bool IsCatalogEmpty => PinnedRows.Count == 0 && Rows.Count == 0;

    public Visibility PinnedHeaderVisibility => HasPinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinnedContentVisibility =>
        HasPinned && !PinnedSectionCollapsed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MainHeaderVisibility => IsCatalogEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MainContentVisibility => MainSectionCollapsed ? Visibility.Collapsed : Visibility.Visible;

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
            _state = await _store.SaveAsync(_state with { PinnedSectionCollapsed = PinnedSectionCollapsed });
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
            _state = await _store.SaveAsync(_state with { MainSectionCollapsed = MainSectionCollapsed });
        }
    }

    private void CatalogArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePinnedSectionLayout();

    // Sizes the pinned scroll region to match the active view. Grid view grows with content but caps at
    // roughly half the catalog area (scrolling within itself past that) so it never crowds the main list
    // out; when the main section is collapsed the cap is lifted. List view is a fixed-height sideways strip.
    private void UpdatePinnedSectionLayout()
    {
        if (PinnedScroll is null)
        {
            return;
        }

        if (IsGridMode)
        {
            PinnedScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            PinnedScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            PinnedScroll.Height = double.NaN;
            var available = CatalogArea?.ActualHeight ?? 0;
            PinnedScroll.MaxHeight = MainSectionCollapsed || available <= 0
                ? double.PositiveInfinity
                : Math.Max(GridTileHeight + 24, available * 0.5);
        }
        else
        {
            PinnedScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            PinnedScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            PinnedScroll.MaxHeight = double.PositiveInfinity;
            PinnedScroll.Height = PinnedStripHeight;
        }
    }
}
