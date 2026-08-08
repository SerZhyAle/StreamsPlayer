using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public sealed class ChannelRow : INotifyPropertyChanged
{
    private FaviconAtlasSet _atlases;
    private ImageSource? _favicon;
    private ImageSource? _preview;
    private bool _faviconLoaded;
    private bool? _previewReachable;
    private bool _isSelected;
    private bool _isTileHovered;
    private bool _isPlayingAudio;

    internal ChannelRow(StreamChannel channel, FaviconAtlasSet atlases)
    {
        Channel = channel;
        _atlases = atlases;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public StreamChannel Channel { get; private set; }
    public ImageSource? Favicon
    {
        get
        {
            if (!_faviconLoaded)
            {
                var (path, maximumIndex) = _atlases.Resolve(Channel.FaviconSource);
                _favicon = FaviconTileLoader.Load(path, Channel.FaviconIndex, maximumIndex);
                _faviconLoaded = true;
            }

            return _favicon;
        }
    }

    public ImageSource? TileImage => _preview ?? Favicon;
    public bool IsSelected
    {
        get => _isSelected;
        private set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public void SetSelected(bool selected) => IsSelected = selected;

    public bool IsTileHovered
    {
        get => _isTileHovered;
        private set
        {
            if (_isTileHovered == value)
            {
                return;
            }

            _isTileHovered = value;
            OnPropertyChanged(nameof(IsTileHovered));
        }
    }

    public void SetTileHovered(bool hovered) => IsTileHovered = hovered;

    public bool IsPlayingAudio
    {
        get => _isPlayingAudio;
        private set
        {
            if (_isPlayingAudio == value)
            {
                return;
            }

            _isPlayingAudio = value;
            OnPropertyChanged(nameof(IsPlayingAudio));
        }
    }

    public void SetPlayingAudio(bool playing) => IsPlayingAudio = playing;

    internal void UpdatePresentation(FaviconAtlasSet atlases)
    {
        if (_atlases == atlases)
        {
            return;
        }

        _atlases = atlases;
        InvalidateFavicon();
    }

    public void UpdateChannel(StreamChannel channel)
    {
        if (Channel == channel)
        {
            return;
        }

        // SP-0052: a row the snapshot re-stamped keeps the same identity but now indexes a different
        // atlas. The blanket notification below re-reads Favicon, but the decoded tile is cached behind
        // _faviconLoaded, so without dropping it the row would keep showing the previous atlas's icon.
        var iconChanged = Channel.FaviconIndex != channel.FaviconIndex ||
            Channel.FaviconSource != channel.FaviconSource;
        Channel = channel;
        if (iconChanged)
        {
            _favicon = null;
            _faviconLoaded = false;
        }

        InvalidateDerivedText();
        OnPropertyChanged(string.Empty);
    }

    private void InvalidateFavicon()
    {
        _favicon = null;
        _faviconLoaded = false;
        OnPropertyChanged(nameof(Favicon));
        if (_preview is null)
        {
            OnPropertyChanged(nameof(TileImage));
        }
    }

    public void SetPreview(ImageSource image, bool? reachable)
    {
        _preview = image;
        if (reachable is not null)
        {
            _previewReachable = reachable;
        }
        OnPropertyChanged(nameof(TileImage));
        OnPropertyChanged(nameof(PreviewStatusBrush));
        OnPropertyChanged(nameof(PreviewStatusLabel));
    }

    public void ClearPreview()
    {
        _preview = null;
        _previewReachable = null;
        OnPropertyChanged(nameof(TileImage));
        OnPropertyChanged(nameof(PreviewStatusBrush));
        OnPropertyChanged(nameof(PreviewStatusLabel));
    }

    public void RefreshLocalization()
    {
        // Both cached strings are built from translated labels, so a language change invalidates them.
        InvalidateDerivedText();
        OnPropertyChanged(string.Empty);
    }

    public string DisplayTitle => StreamTitleFormatter.Display(Channel.Title);
    public string KindLabel => LocalizationService.Get(Channel.MediaKind switch
    {
        MediaKind.Audio => "KindAudio",
        MediaKind.Video => "KindVideo",
        _ => "KindRtsp"
    });
    public Visibility PinnedVisibility => Channel.Pinned ? Visibility.Visible : Visibility.Collapsed;

    // SP-0033: deliberately not folded into Metadata/TechnicalDetails - those are neutral maintainer
    // claims, while this is a caveat that has to read as distinct rather than as one more fragment.
    public Visibility RegionRestrictedVisibility =>
        Channel.Access == ChannelAccess.GeoRestricted ? Visibility.Visible : Visibility.Collapsed;
    public string RegionRestrictedLabel => LocalizationService.Get("RegionRestrictedLabel");
    public string RegionRestrictedTip => LocalizationService.Get("RegionRestrictedTip");
    // SP-0067: both derived strings are computed once per render instead of on every read. The card
    // template reads Metadata twice and TechnicalDetails three times (once more through
    // TechnicalDetailsVisibility), and each read was a LINQ filter plus a string.Join - on every one of
    // the tens of cards realized per viewport, on every scroll. The cache is dropped in the two places
    // that already exist to say "this row now renders differently": UpdateChannel and
    // RefreshLocalization. There is no third invalidation point to remember.
    private string? _metadata;
    private string? _technicalDetails;

    // SP-0061: the rubric is shown translated; an identifier outside the bank's closed set falls through
    // as written. RefreshLocalization re-renders the row when the interface language changes.
    public string Metadata => _metadata ??= string.Join("  ·  ",
        new[] { KindLabel, TopicLabels.Text(Channel.Topic), Channel.Country, Channel.Language }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    // SP-0018: compact, present-only technical claims. Absent when the catalog supplied none, so the
    // default card is never crowded; these are untrusted maintainer claims, not measured quality.
    public string TechnicalDetails => _technicalDetails ??= string.Join("  ·  ", new[]
    {
        Channel.Format?.Trim().ToUpperInvariant(),
        BitrateLabel(),
        Channel.Protocol?.Trim().ToUpperInvariant(),
        LiveLabel()
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public Visibility TechnicalDetailsVisibility =>
        TechnicalDetails.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    private void InvalidateDerivedText()
    {
        _metadata = null;
        _technicalDetails = null;
    }

    private string? BitrateLabel()
    {
        if (string.IsNullOrWhiteSpace(Channel.Bitrate))
        {
            return null;
        }

        return StreamBitrate.TryParseKbps(Channel.Bitrate, out var kbps)
            ? LocalizationService.Format("BitrateValue", kbps)
            : Channel.Bitrate.Trim();
    }

    private string? LiveLabel() => Channel.IsLive switch
    {
        true => LocalizationService.Get("LiveLabel"),
        false => LocalizationService.Get("OnDemandLabel"),
        _ => null
    };
    public string StatusLabel => Channel.LastPlayOutcome switch
    {
        PlayOutcome.Ok => LocalizationService.Get("StatusVerified"),
        PlayOutcome.Fail => LocalizationService.Get("StatusFailed"),
        _ => LocalizationService.Get("StatusNotPlayed")
    };
    public Brush StatusBrush => Channel.LastPlayOutcome switch
    {
        PlayOutcome.Ok => Brushes.ForestGreen,
        PlayOutcome.Fail => Brushes.Firebrick,
        _ => Brushes.DarkGoldenrod
    };
    public Brush PreviewStatusBrush => _previewReachable == true ? Brushes.LimeGreen : Brushes.Goldenrod;
    public string PreviewStatusLabel => LocalizationService.Get(_previewReachable == true ? "PreviewCaptured" : "PreviewNotCaptured");

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// One row of the catalog grid: the cards in it, and how many columns the row is laid out in.
/// </summary>
/// <remarks>
/// SP-0067 turned this from a record into a mutable notifying class. A column-count change re-chunks
/// every row, and as a record that meant allocating a fresh instance and a fresh array for each of
/// 2481 to 6616 rows, five times in each direction of one window drag - measured on the owner's
/// catalog. The bindings in <c>MainWindow.xaml</c> (<c>ItemsSource</c>, <c>Columns</c>) follow the
/// mutation instead, so a re-chunk assigns rather than allocates.
/// <para>
/// Consequence worth knowing: reference equality replaces value equality. Everything that relies on
/// finding an instance in <c>GridRows</c> - <c>ScrollIntoView</c> in the keyboard handler - still works,
/// because the list holds the very objects it is handed. Nothing compares two grid rows for equality.
/// </para>
/// </remarks>
public sealed class CatalogGridRow(IReadOnlyList<ChannelRow> items, int columnCount) : INotifyPropertyChanged
{
    private IReadOnlyList<ChannelRow> _items = items;
    private int _columnCount = columnCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ChannelRow> Items => _items;
    public int ColumnCount => _columnCount;

    /// <summary>Re-points this row at a new slice, raising a change only for what actually changed.</summary>
    public void Update(IReadOnlyList<ChannelRow> items, int columnCount)
    {
        if (!ReferenceEquals(_items, items))
        {
            _items = items;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Items)));
        }

        if (_columnCount != columnCount)
        {
            _columnCount = columnCount;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColumnCount)));
        }
    }
}
