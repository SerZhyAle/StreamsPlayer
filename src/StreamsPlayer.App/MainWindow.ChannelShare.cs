using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0058: handing one channel to another person as an ordinary chat message, and taking one back.
/// Core owns the format (<see cref="ChannelShareText"/>); this file owns the three things it cannot -
/// the clipboard, the menus, and the confirmations.
/// </summary>
public partial class MainWindow
{
    private void CopyShareTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not ChannelRow row)
        {
            return;
        }

        // Mandatory, unlike the export gate it mirrors: a chat message is not revocable and is a strictly
        // easier leak path than a file the user chose where to save.
        if (CatalogUrlIdentity.HasCredentials(row.Channel.Url) &&
            MessageBox.Show(this, LocalizationService.Get("ShareCredentialWarning"),
                LocalizationService.Get("MenuCopyShareText"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Clipboard.SetText(ChannelShareText.Format(row.Channel.Url));
            _log.Event("SHARE COPY", $"url={CatalogUrlIdentity.Redact(row.Channel.Url)}");
            MessageBox.Show(this, LocalizationService.Get("ShareTextCopied"),
                LocalizationService.Get("MenuCopyShareText"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (COMException)
        {
            // Another process owns the clipboard; the same failure ChannelInfoWindow reports for its copy.
            MessageBox.Show(this, LocalizationService.Get("ShareTextCopyFailed"),
                LocalizationService.Get("MenuCopyShareText"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PasteChannelButton_Click(object sender, RoutedEventArgs e) => await PasteChannelAsync();

    /// <summary>
    /// Reads the clipboard, explains whatever it found, and applies only on confirmation. Nothing here
    /// touches the network: the address is taken at face value and a bad one surfaces later as an
    /// ordinary playback failure, which the product already explains.
    /// </summary>
    private async Task PasteChannelAsync()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        // The write path only has to survive another process owning the clipboard. A *read* can also be
        // handed a malformed payload, which the shell surfaces as OutOfMemoryException rather than a COM
        // error - so the copy action's narrower filter is not enough here.
        catch (Exception exception) when (exception is COMException or OutOfMemoryException)
        {
            _log.Event("SHARE PASTE FAIL", exception.GetType().Name);
            MessageBox.Show(this, LocalizationService.Get("PasteClipboardFailed"),
                LocalizationService.Get("PasteChannelPlain"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var read = ChannelShareText.Read(text);
        if (read.Status != ChannelShareStatus.Ok)
        {
            _log.Event("SHARE PASTE SKIP", $"status={read.Status}");
            // An empty clipboard and unrelated text are the same situation to the user - there is nothing
            // here to add - so they deliberately share one message.
            var key = read.Status switch
            {
                ChannelShareStatus.UnsupportedVersion => "PasteUnsupportedVersion",
                ChannelShareStatus.InvalidAddress => "PasteInvalidAddress",
                _ => "PasteNotShareText"
            };
            MessageBox.Show(this, LocalizationService.Get(key), LocalizationService.Get("PasteChannelPlain"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var match = ChannelShareText.FindExisting(_state.Channels, _state.HiddenCatalogUrls, read.Url);
        if (match.Existing is { } existing)
        {
            await ShowExistingChannelAsync(existing, match.Hidden);
            return;
        }

        await AddSharedChannelAsync(read.Url);
    }

    /// <summary>
    /// The address is already in the list. Nothing is added, whatever the row's provenance - the user is
    /// taken to what they have, and offered a restore when they had hidden it.
    /// </summary>
    private async Task ShowExistingChannelAsync(StreamChannel existing, bool hidden)
    {
        var title = StreamTitleFormatter.Display(existing.Title);
        if (!hidden)
        {
            SetStatus("PasteChannelAlready", title);
            await RevealChannelAsync(existing.Id);
            return;
        }

        if (MessageBox.Show(this, LocalizationService.Format("PasteChannelHidden", title),
                LocalizationService.Get("PasteChannelPlain"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        // UnhideAsync already persists, re-facets and re-filters; repeating any of that here would only
        // write the state twice.
        await UnhideAsync(existing.Url);
        SetStatus("PasteChannelRestored", title);
        await RevealChannelAsync(existing.Id);
    }

    /// <summary>
    /// A genuinely new address. Title and media kind are derived locally by the same two rules the M3U
    /// import applies, and the row is written in a single persisted change so the apply is atomic.
    /// </summary>
    private async Task AddSharedChannelAsync(string url)
    {
        var title = new Uri(url).Host;
        var kind = StreamMediaKindClassifier.Classify(url);

        // A message box rather than ImportPreviewWindow: the payload is one title and one address, and a
        // list-shaped preview reads wrong for a single channel.
        if (MessageBox.Show(this,
                LocalizationService.Format("PasteChannelConfirm", title, CatalogUrlIdentity.Redact(url)),
                LocalizationService.Get("PasteChannelPlain"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        var nextOrder = _state.Channels.Count == 0 ? 0 : _state.Channels.Max(channel => channel.SortIndex) + 1;
        // Imported, not Manual: the catalog merge only ever updates or removes rows whose origin is
        // Catalog, so this is what makes the pasted channel survive every later refresh.
        var channel = new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = title,
            MediaKind = kind,
            SourceOrigin = SourceOrigin.Imported,
            SortIndex = nextOrder,
            AddedAt = DateTimeOffset.UtcNow
        };

        _state = await PersistAsync(_state with { Channels = [.. _state.Channels, channel] });
        _log.Event("SHARE PASTE APPLY", $"url={CatalogUrlIdentity.Redact(url)}");
        PopulateFacets();
        ApplyFilter();
        SetStatus("AddedStream", title);
        await RevealChannelAsync(channel.Id);
    }
}
