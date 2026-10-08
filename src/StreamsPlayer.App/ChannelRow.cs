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
    private WaveParticlesBackdropSession? _backdrop;

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

    // SP-0087: what the row shows when the bank gave this channel no icon - 71.2% of the published rows
    // on 2026-08-19. Each visibility reads the image property it guards rather than a flag of its own,
    // so the fallback and the picture are decided in one pass and can never disagree; and
    // TileFallbackVisibility reading TileImage is what lets a captured preview frame win over the
    // monogram without a second rule anywhere.
    public Visibility FaviconFallbackVisibility =>
        Favicon is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TileFallbackVisibility =>
        TileImage is null ? Visibility.Visible : Visibility.Collapsed;
    public string MonogramText => _monogram ??= ChannelMonogram.Text(Channel.Title);
    public Brush MonogramBrush => MonogramPalette.Plate(Channel.Title);
    public Brush MonogramForeground => MonogramPalette.Foreground;

    // Cached behind its own flag rather than behind the value: null is a legitimate answer here - the
    // bank leaves country blank on 62% of rows and spells it 54 different ways on some of the rest - so
    // a `??=` would re-run the lookup on every read for exactly the rows where it fails.
    public string? CountryCode
    {
        get
        {
            if (!_countryResolved)
            {
                _countryCode = CatalogCountries.ToCode(Channel.Country);
                _countryResolved = true;
            }

            return _countryCode;
        }
    }

    public Visibility CountryCodeVisibility =>
        CountryCode is null ? Visibility.Collapsed : Visibility.Visible;

    // The flag stands in for the code on the monogram plate; a country this build has no flag for keeps the
    // code as text, so a known country never disappears from the plate. The full name, in the interface
    // language, is the tooltip. Not cached: the language can change while the row lives, the lookup is a
    // dictionary read, and CountryFlags caches the decoded bitmap itself.
    public ImageSource? CountryFlag => CountryFlags.For(CountryCode);

    public Visibility CountryFlagVisibility =>
        CountryFlag is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CountryCodeTextVisibility =>
        CountryCode is not null && CountryFlag is null ? Visibility.Visible : Visibility.Collapsed;

    public string? CountryName =>
        CountryCode is { } code ? CountryNames.Label(code, LocalizationService.CurrentLanguage) : null;

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

    /// <summary>
    /// SP-0110: the animated backdrop of the playing station, or null. Set only while the station plays
    /// and the setting allows it, so the card and tile templates need no second condition.
    /// </summary>
    public WaveParticlesBackdropSession? Backdrop
    {
        get => _backdrop;
        private set
        {
            if (ReferenceEquals(_backdrop, value))
            {
                return;
            }

            _backdrop = value;
            OnPropertyChanged(nameof(Backdrop));
            OnPropertyChanged(nameof(HasBackdrop));
        }
    }

    public bool HasBackdrop => _backdrop is not null;

    public void SetBackdrop(WaveParticlesBackdropSession? backdrop) => Backdrop = backdrop;

    // SP-0075: the title of the programme on air, from the user's downloaded TV schedule. Null for every
    // channel without a schedule, which collapses the line and leaves the card exactly as it was.
    private string? _scheduleNow;

    public void SetScheduleNow(string? title)
    {
        if (string.Equals(_scheduleNow, title, StringComparison.Ordinal))
        {
            return;
        }

        _scheduleNow = title;
        OnPropertyChanged(nameof(ScheduleNowText));
        OnPropertyChanged(nameof(ScheduleNowVisibility));
    }

    // Formatted on read so a language change (RefreshLocalization) re-renders it with no cache to drop.
    // The title itself is the source's text and is never translated.
    public string ScheduleNowText =>
        _scheduleNow is null ? string.Empty : LocalizationService.Format("TvScheduleNowLine", _scheduleNow);

    public Visibility ScheduleNowVisibility => _scheduleNow is null ? Visibility.Collapsed : Visibility.Visible;

    internal void UpdatePresentation(FaviconAtlasSet atlases)
    {
        if (_atlases == atlases)
        {
            return;
        }

        _atlases = atlases;
        InvalidateFavicon();
    }

    /// <summary>
    /// SP-0125: a row read while its sheet was still decoding cached "no icon"; once a sheet is ready it
    /// asks again. Rows that already have an icon, or never had an index, have nothing to gain.
    /// </summary>
    internal void RefreshPendingFavicon()
    {
        if (_faviconLoaded && _favicon is null && Channel.FaviconIndex is not null)
        {
            InvalidateFavicon();
        }
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
        // Release audit 26.1001.0140: the captured still belongs to the address it was taken from. Every
        // ClearPreview caller looks a row up by URL, and an edited row is re-keyed, so none would reach it.
        var addressChanged = !string.Equals(Channel.Url, channel.Url, StringComparison.Ordinal);
        Channel = channel;
        if (addressChanged)
        {
            _preview = null;
            _previewReachable = null;
        }

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
        OnPropertyChanged(nameof(FaviconFallbackVisibility));
        if (_preview is null)
        {
            OnPropertyChanged(nameof(TileImage));
            OnPropertyChanged(nameof(TileFallbackVisibility));
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
        OnPropertyChanged(nameof(TileFallbackVisibility));
        OnPropertyChanged(nameof(PreviewStatusLabel));
    }

    public void ClearPreview()
    {
        _preview = null;
        _previewReachable = null;
        OnPropertyChanged(nameof(TileImage));
        OnPropertyChanged(nameof(TileFallbackVisibility));
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
    private string? _metadataHead;
    private string? _metadataTail;
    private string? _tags;
    private string? _technicalDetails;
    // SP-0087: the monogram and the country code are derived from Channel in exactly the same way, so
    // they live in the same cache and are dropped at the same point. There is no third invalidation
    // site to remember.
    private string? _monogram;
    private string? _countryCode;
    private bool _countryResolved;

    // SP-0061: the rubric is shown translated; an identifier outside the bank's closed set falls through
    // as written. RefreshLocalization re-renders the row when the interface language changes.
    // The card draws the country as a flag between two text runs (MetadataHead, then the flag cell, then
    // MetadataTail), so no single string can hold its line; Metadata is the same line as plain text, with
    // the country spelled out, for the tooltip - the full name is what the flag stands for.
    public string Metadata => _metadata ??= string.Join("  ·  ",
        new[] { KindLabel, TopicLabels.Text(Channel.Topic), CountryName ?? Channel.Country?.Trim(), Channel.Language }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>What precedes the country on the card: the media kind and the rubric.</summary>
    public string MetadataHead => _metadataHead ??= string.Join("  ·  ",
        new[] { KindLabel, TopicLabels.Text(Channel.Topic) }.Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>What follows the country on the card: the broadcast language, led by its separator when anything precedes it.</summary>
    public string MetadataTail => _metadataTail ??=
        string.IsNullOrWhiteSpace(Channel.Language)
            ? string.Empty
            : (MetadataHead.Length > 0 || !string.IsNullOrWhiteSpace(Channel.Country) ? "  ·  " : string.Empty) + Channel.Language.Trim();

    /// <summary>The country cell: its separator, then the flag - or the code as text when this build has no flag for it.</summary>
    public Visibility CountryInlineVisibility =>
        string.IsNullOrWhiteSpace(Channel.Country) ? Visibility.Collapsed : Visibility.Visible;

    public string CountrySeparator => MetadataHead.Length > 0 ? "  ·  " : string.Empty;

    /// <summary>The country as text, only when there is no flag to draw: the code, or the bank's own spelling when it resolves to none.</summary>
    public string? CountryInlineText =>
        CountryFlag is null ? CountryCode ?? Channel.Country?.Trim() : null;

    public Visibility CountryInlineTextVisibility =>
        string.IsNullOrWhiteSpace(CountryInlineText) ? Visibility.Collapsed : Visibility.Visible;

    // The station's own descriptors without the media kind. The compact panel shows only radio, so the
    // kind would be the same word on every station; the card prefixes it through Metadata above.
    public string Tags => _tags ??= string.Join("  ·  ",
        new[] { TopicLabels.Text(Channel.Topic), Channel.Country, Channel.Language }
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
        _metadataHead = null;
        _metadataTail = null;
        _tags = null;
        _technicalDetails = null;
        _monogram = null;
        _countryCode = null;
        _countryResolved = false;
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

    // SP-0099: a FastMediaSorter hand-off or phone/watch address is live by contract, whatever its row says.
    // SP-0201 requirement 4: a channel that came from the directory and whose broadcast left stays in the
    // library, marked ended - the one word the row says about it, in place of the live label it carried.
    private string? LiveLabel() => FastMediaSorterBroadcastImport.IsFastMediaSorterBroadcast(Channel)
        ? Channel.FastMediaSorterBroadcast?.DirectoryEndedAt is null
            ? LocalizationService.Get("LiveLabel")
            : LocalizationService.Get("EndedLabel")
        : Channel.IsLive switch
    {
        true => LocalizationService.Get("LiveLabel"),
        false => LocalizationService.Get("OnDemandLabel"),
        _ => null
    };
    public string StatusLabel => LocalizationService.Get(ChannelFactSheet.OutcomeKey(Channel));
    // SP-0114: the status dot's palette role, not its colour. A brush chosen here would be a literal the
    // theme cannot reach; the templates map the role to SuccessBrush / DangerBrush / WarningBrush by
    // dynamic reference, so the dot follows a theme switch like everything around it (APP-STYLE 3).
    // SP-0124: an address that is never launched reads as a failure before it is ever tried.
    public string StatusTone => !LaunchableAddress.IsLaunchable(Channel.Url)
        ? "Danger"
        : Channel.LastPlayOutcome switch
        {
            PlayOutcome.Ok => "Success",
            PlayOutcome.Fail => "Danger",
            _ => "Warning"
        };
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
