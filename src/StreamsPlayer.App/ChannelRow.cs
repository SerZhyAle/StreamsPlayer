using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public sealed class ChannelRow : INotifyPropertyChanged
{
    private string? _atlasPath;
    private int? _maximumFaviconIndex;
    private ImageSource? _favicon;
    private ImageSource? _preview;
    private bool _faviconLoaded;
    private bool? _previewReachable;
    private bool _isSelected;
    private bool _isPlayingAudio;

    public ChannelRow(StreamChannel channel, string? atlasPath, int? maximumFaviconIndex)
    {
        Channel = channel;
        _atlasPath = atlasPath;
        _maximumFaviconIndex = maximumFaviconIndex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public StreamChannel Channel { get; private set; }
    public ImageSource? Favicon
    {
        get
        {
            if (!_faviconLoaded)
            {
                _favicon = FaviconTileLoader.Load(_atlasPath, Channel.FaviconIndex, _maximumFaviconIndex);
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

    public void UpdatePresentation(string? atlasPath, int? maximumFaviconIndex)
    {
        if (string.Equals(_atlasPath, atlasPath, StringComparison.OrdinalIgnoreCase) &&
            _maximumFaviconIndex == maximumFaviconIndex)
        {
            return;
        }

        _atlasPath = atlasPath;
        _maximumFaviconIndex = maximumFaviconIndex;
        _favicon = null;
        _faviconLoaded = false;
        OnPropertyChanged(nameof(Favicon));
        if (_preview is null)
        {
            OnPropertyChanged(nameof(TileImage));
        }
    }

    public void UpdateChannel(StreamChannel channel)
    {
        if (Channel == channel)
        {
            return;
        }

        Channel = channel;
        OnPropertyChanged(string.Empty);
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

    public void RefreshLocalization() => OnPropertyChanged(string.Empty);

    public string DisplayTitle => StreamTitleFormatter.Display(Channel.Title);
    public string KindLabel => LocalizationService.Get(Channel.MediaKind switch
    {
        MediaKind.Audio => "KindAudio",
        MediaKind.Video => "KindVideo",
        _ => "KindRtsp"
    });
    public Visibility PinnedVisibility => Channel.Pinned ? Visibility.Visible : Visibility.Collapsed;
    public string Metadata => string.Join("  ·  ", new[] { KindLabel, Channel.Topic, Channel.Country, Channel.Language }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    // SP-0018: compact, present-only technical claims. Absent when the catalog supplied none, so the
    // default card is never crowded; these are untrusted maintainer claims, not measured quality.
    public string TechnicalDetails => string.Join("  ·  ", new[]
    {
        Channel.Format?.Trim().ToUpperInvariant(),
        BitrateLabel(),
        Channel.Protocol?.Trim().ToUpperInvariant(),
        LiveLabel()
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public Visibility TechnicalDetailsVisibility =>
        TechnicalDetails.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    private string? BitrateLabel()
    {
        if (string.IsNullOrWhiteSpace(Channel.Bitrate))
        {
            return null;
        }

        return StreamBitrate.TryParseKbps(Channel.Bitrate, out var kbps)
            ? string.Format(LocalizationService.Get("BitrateValue"), kbps)
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

public sealed record CatalogGridRow(IReadOnlyList<ChannelRow> Items, int ColumnCount);
