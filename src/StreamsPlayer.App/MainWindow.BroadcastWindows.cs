using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>How an attempt to open an incoming broadcast in a window of its own ended.</summary>
internal enum BroadcastOpenOutcome
{
    Opened,
    AlreadyOpen,
    BoundReached,
    Declined,
    Refused,
    NotABroadcastLink
}

/// <param name="MessageKey">The localization key of what the list says when the outcome is a refusal, or null.</param>
internal sealed record BroadcastOpenResult(BroadcastOpenOutcome Outcome, string? MessageKey = null);

/// <summary>One incoming broadcast that has a window open now, as the list shows it.</summary>
internal sealed record IncomingBroadcastEntry(
    Guid ChannelId,
    string Title,
    bool OpenedByLink,
    BroadcastLegState State,
    string? MessageKey,
    string? SourceId);

/// <summary>
/// SP-0222 requirement 3: an incoming broadcast plays in a window of its own and never through the main window's
/// one audio session, so any number play together, each with its own volume and stop, and the station playing in
/// the main window is left alone. A video broadcast opens the product's player window; an audio-only broadcast
/// opens <see cref="BroadcastAudioWindow"/>. The number open at once is bounded by
/// <see cref="IncomingBroadcastWindows.Default"/>, and a window gives its place back when it closes.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<Guid, IncomingWindow> _incomingWindows = [];

    private sealed record IncomingWindow(
        Window Window,
        BroadcastAudioSession? Session,
        PlayerWindow? Player,
        StreamChannel Channel,
        bool OpenedByLink);

    /// <summary>Raised on the UI thread when a window of an incoming broadcast opens, closes or changes state.</summary>
    internal event Action? IncomingBroadcastsChanged;

    internal IReadOnlyList<IncomingBroadcastEntry> IncomingBroadcasts => [.. _incomingWindows.Values.Select(item =>
        new IncomingBroadcastEntry(
            item.Channel.Id,
            StreamTitleFormatter.Display(item.Channel.Title),
            item.OpenedByLink,
            item.Session?.State ?? item.Player?.BroadcastState ?? BroadcastLegState.Connecting,
            item.Session?.MessageKey,
            item.Channel.FastMediaSorterBroadcast?.SourceId))];

    /// <summary>
    /// Takes a place under the bound, imports through the one path every hand-off uses, and opens the window.
    /// The place is taken first, so a refused open at the bound adds nothing to the person's library.
    /// </summary>
    /// <param name="import">The import of the broadcast into the library; null from it is a refusal it has already explained.</param>
    /// <param name="recordCredentials">The pair the account's record handed this device (item BF / BD), or null.</param>
    private async Task<BroadcastOpenResult> OpenIncomingBroadcastAsync(
        Func<Task<StreamChannel?>> import,
        ExchangeRelayCredentials? recordCredentials,
        bool openedByLink)
    {
        if (!IncomingBroadcastWindows.Default.TryAcquire(out var lease))
        {
            return new BroadcastOpenResult(BroadcastOpenOutcome.BoundReached, IncomingBroadcastFailure.BoundReachedKey);
        }

        var owned = false;
        try
        {
            var channel = await import();
            if (channel is null)
            {
                return new BroadcastOpenResult(BroadcastOpenOutcome.Refused);
            }

            if (_incomingWindows.TryGetValue(channel.Id, out var existing))
            {
                ActivateWindow(existing.Window);
                return new BroadcastOpenResult(BroadcastOpenOutcome.AlreadyOpen);
            }

            // SP-0215 requirement 7 and DEVICE-EXCHANGE item BF: the pair a record hands this device plays its
            // own protected broadcast with no question; any other protected broadcast asks the one prompt (or
            // answers with the password remembered for it) before the window is opened.
            BroadcastCredentialsSession? credentials = null;
            if (IsPasswordProtected(channel))
            {
                var pair = recordCredentials;
                if (pair is null)
                {
                    pair = ResolveBroadcastCredentials(channel, interactive: true, out var declined);
                    if (declined)
                    {
                        // The person dismissed the one prompt: no window, no failure record.
                        return new BroadcastOpenResult(BroadcastOpenOutcome.Declined);
                    }
                }

                credentials = new BroadcastCredentialsSession(pair, ResolveBroadcastCredentialsForRetry, ForgetBroadcastPassword);
            }

            var entry = OpenIncomingWindow(channel, credentials, openedByLink);
            _incomingWindows[channel.Id] = entry;
            owned = true;
            entry.Window.Closed += (_, _) =>
            {
                _incomingWindows.Remove(channel.Id);
                lease!.Dispose();
                IncomingBroadcastsChanged?.Invoke();
            };
            IncomingBroadcastsChanged?.Invoke();
            if (entry.Window is BroadcastAudioWindow audio)
            {
                audio.StartListening();
            }

            return new BroadcastOpenResult(BroadcastOpenOutcome.Opened);
        }
        finally
        {
            if (!owned)
            {
                lease!.Dispose();
            }
        }
    }

    private IncomingWindow OpenIncomingWindow(StreamChannel channel, BroadcastCredentialsSession? credentials, bool openedByLink)
    {
        if (channel.MediaKind == MediaKind.Audio)
        {
            var state = _stateCommitter?.Requested ?? _state;
            var session = new BroadcastAudioSession(channel, credentials, state.AudioVolume, state.AudioOutputDevice,
                state.AudioChannelMode, (tag, fields) => _log.Event(tag, fields));
            session.Changed += () =>
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    Dispatcher.BeginInvoke(() => IncomingBroadcastsChanged?.Invoke());
                }
            };
            var window = new BroadcastAudioWindow(channel, session, state.AudioVolume);
            window.PlaceCascaded(_incomingWindows.Values.Count(item => item.Window is BroadcastAudioWindow) % 8);
            window.Show();
            return new IncomingWindow(window, session, null, channel, openedByLink);
        }

        var player = OpenIndependentPlayerWindow(channel, broadcastCredentials: credentials);
        return new IncomingWindow(player, null, player, channel, openedByLink);
    }

    /// <summary>The list's Play: import as a directory broadcast, then the window; the main window's station is untouched.</summary>
    internal Task<BroadcastOpenResult> OpenExchangeBroadcastWindowAsync(ExchangeBroadcastView view) =>
        OpenIncomingBroadcastAsync(() => ImportExchangeBroadcastAsync(view), view.Record.Credentials, openedByLink: false);

    /// <summary>
    /// The list's Play for a broadcast given to this PC as a guest. It is imported like a link, with no directory
    /// id: a guest's broadcast is not one of the own account's, and the own directory's marking of ended rows
    /// reads only the own account's, so a row carrying a guest broadcast's id would be marked ended by every
    /// own-account push.
    /// </summary>
    internal Task<BroadcastOpenResult> OpenGuestBroadcastWindowAsync(ExchangeBroadcastView view)
    {
        if (view.Support != ExchangeBroadcastSupport.Supported || view.Record.Descriptor is not { } descriptor)
        {
            return Task.FromResult(new BroadcastOpenResult(BroadcastOpenOutcome.Refused,
                view.Support == ExchangeBroadcastSupport.UnsupportedSchema
                    ? ExchangeFailureRows.FormatUnsupported.MessageKey : "FmsBroadcastUnsupportedMode"));
        }

        return OpenIncomingBroadcastAsync(() => ImportLinkBroadcastAsync(descriptor), view.Record.Credentials, openedByLink: false);
    }

    /// <summary>
    /// The door for a pasted link: the text is read by the one reader, imported as a hand-off channel, and opened.
    /// A text that is not a broadcast link is said to be one that is not; a link this build cannot read says why.
    /// </summary>
    internal async Task<BroadcastOpenResult> OpenBroadcastByLinkAsync(string text)
    {
        var read = FastMediaSorterBroadcastDescriptor.Read(text);
        RememberLinkKeys(read.Broadcast);
        if (read.Status == FastMediaSorterBroadcastReadStatus.NotBroadcast)
        {
            return new BroadcastOpenResult(BroadcastOpenOutcome.NotABroadcastLink, "BroadcastsLinkInvalid");
        }

        if (!read.IsAccepted || read.Broadcast is null)
        {
            _log.Event("FMS IMPORT SKIP", $"status={read.Status}", "contract=LIVE-BROADCAST");
            return new BroadcastOpenResult(BroadcastOpenOutcome.Refused, read.Status switch
            {
                FastMediaSorterBroadcastReadStatus.UnsupportedSchema => ExchangeFailureRows.FormatUnsupported.MessageKey,
                FastMediaSorterBroadcastReadStatus.UnsupportedMode => "FmsBroadcastUnsupportedMode",
                FastMediaSorterBroadcastReadStatus.TooLarge => "FmsBroadcastTooLarge",
                _ => "FmsBroadcastInvalid"
            });
        }

        var broadcast = read.Broadcast;
        return await OpenIncomingBroadcastAsync(() => ImportLinkBroadcastAsync(broadcast), recordCredentials: null, openedByLink: true);
    }

    private async Task<StreamChannel?> ImportLinkBroadcastAsync(FastMediaSorterBroadcast broadcast)
    {
        if (_busy)
        {
            SetStatus("FmsBroadcastBusy");
            return null;
        }

        FastMediaSorterBroadcastApplyResult? applied = null;
        await PersistAsync(state =>
        {
            applied = FastMediaSorterBroadcastImport.Apply(state.Channels, broadcast, DateTimeOffset.UtcNow);
            return state with { Channels = [.. applied.Channels] };
        });
        if (applied is null)
        {
            return null;
        }

        _log.Event("FMS IMPORT APPLY", $"added={applied.Added}", $"url={CatalogUrlIdentity.Redact(applied.Channel.Url)}", "source=link");
        PopulateFacets();
        ApplyFilter();
        SetStatus(applied.Added ? "FmsBroadcastAdded" : "FmsBroadcastUpdated", applied.Channel.Title);
        return applied.Channel;
    }

    private static void ActivateWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>Closes every incoming broadcast's audio window; the player windows are closed with the others.</summary>
    private void CloseIncomingBroadcastWindows()
    {
        foreach (var item in _incomingWindows.Values.ToArray())
        {
            if (item.Window is BroadcastAudioWindow)
            {
                item.Window.Close();
            }
        }
    }
}
