using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private ExchangeSourceService Exchange => ((App)Application.Current).Exchange;

    internal void InitializeExchangeStatus(ExchangeSourceService source)
    {
        void Refresh()
        {
            ExchangeSourceStatusButton.Visibility = source.Account.Token is not null || source.Account.Host.Length > 0
                || source.StatusKey == "ExchangeEnrollAgain" ? Visibility.Visible : Visibility.Collapsed;
            // SP-0201: the broadcasts view exists for an enrolled device - the account section is where a
            // device without one is told why, so the button is not shown to it.
            BroadcastsButton.Visibility = source.Account.Token is not null ? Visibility.Visible : Visibility.Collapsed;
            ExchangeSourceStatusText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, source.StatusKey);
        }

        void Changed()
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(Refresh);
            }
        }

        void DirectoryChanged()
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(ApplyExchangeDirectory);
            }
        }

        source.Changed += Changed;
        source.DirectoryChanged += DirectoryChanged;
        source.CastPromptRequested += PromptCastOfferAsync;
        source.CastAccepted += PlayCastOffer;
        source.CastStopped += StopCast;
        Closed += (_, _) =>
        {
            source.Changed -= Changed;
            source.DirectoryChanged -= DirectoryChanged;
            source.CastPromptRequested -= PromptCastOfferAsync;
            source.CastAccepted -= PlayCastOffer;
            source.CastStopped -= StopCast;
        };
        Refresh();
    }

    private void ExchangeSourceStatus_Click(object sender, RoutedEventArgs e) => OpenSettings(1);

    private void BroadcastsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new BroadcastsWindow(Exchange, PlayExchangeBroadcastAsync, KeepExchangeBroadcastAsync)
            {
                Owner = DialogOwner
            };
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(BroadcastsButton_Click), exception);
        }
    }

    /// <summary>
    /// SP-0201 requirements 1 and 4, on every directory frame: a record that left marks its channel ended
    /// (kept, never deleted), a record that stayed refreshes what the user already kept by sourceId, and
    /// nothing is ever added from a background push - Option A of the plan's open question 1, as the owner
    /// answered it. The window being closed changes none of this: the library rules bind whatever is open.
    /// </summary>
    private async void ApplyExchangeDirectory()
    {
        try
        {
            if (_busy)
            {
                // A user operation owns the window; the next frame, or the full list a reconnect sends,
                // applies what this one carried. Never queued: a queued mutation would land on a state
                // the operation that owns the window may have already replaced.
                return;
            }

            var snapshot = Exchange.Directory;
            if (snapshot is null)
            {
                return;
            }

            var broadcasts = snapshot.Groups.SelectMany(group => group.Broadcasts).ToList();
            var changed = false;
            await PersistAsync(state =>
            {
                var refreshed = ExchangeDirectoryChannels.RefreshStored(state.Channels, broadcasts, DateTimeOffset.UtcNow);
                var ended = ExchangeDirectoryChannels.MarkEnded(
                    refreshed, snapshot.LiveSourceIds, snapshot.LiveBroadcastIds, DateTimeOffset.UtcNow);
                var byId = ended.ToDictionary(row => row.Id);
                var channels = refreshed
                    .Select(channel => byId.TryGetValue(channel.Id, out var marked) ? marked : channel)
                    .ToList();
                if (channels.SequenceEqual(state.Channels))
                {
                    return state;
                }

                changed = true;
                return state with { Channels = channels };
            });

            if (changed)
            {
                PopulateFacets();
                ApplyFilter();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(ApplyExchangeDirectory), exception);
        }
    }

    /// <summary>The view's Play: the same import a link runs, then the ordinary play of the channel.</summary>
    private async Task PlayExchangeBroadcastAsync(ExchangeBroadcastView view)
    {
        var channel = await ImportExchangeBroadcastAsync(view);
        if (channel is not null)
        {
            await PlayChannelAsync(channel, rememberSelection: true);
        }
    }

    /// <summary>The view's Keep: the same import a link runs, then the reveal of the row it made.</summary>
    private async Task KeepExchangeBroadcastAsync(ExchangeBroadcastView view)
    {
        var channel = await ImportExchangeBroadcastAsync(view);
        if (channel is not null)
        {
            await RevealChannelAsync(channel.Id);
        }
    }

    private async Task<StreamChannel?> ImportExchangeBroadcastAsync(ExchangeBroadcastView view)
    {
        if (_busy)
        {
            SetStatus("FmsBroadcastBusy");
            return null;
        }

        if (view.Support != ExchangeBroadcastSupport.Supported || view.Record.Descriptor is not { } descriptor)
        {
            // The view never offers these a Play or Keep; this is the unreachable second guard.
            SetStatus(view.Support == ExchangeBroadcastSupport.UnsupportedSchema
                ? "FmsBroadcastUnsupportedSchema" : "FmsBroadcastUnsupportedMode");
            return null;
        }

        FastMediaSorterBroadcastApplyResult? applied = null;
        await PersistAsync(state =>
        {
            // Playing or keeping one is the explicit act that creates the channel (Option A); the record's
            // broadcastId is what makes the row the directory's, and therefore the one a leaving record marks.
            applied = FastMediaSorterBroadcastImport.Apply(
                state.Channels, descriptor, DateTimeOffset.UtcNow, view.Record.BroadcastId);
            return state with { Channels = [.. applied.Channels] };
        });
        if (applied is null)
        {
            return null;
        }

        PopulateFacets();
        ApplyFilter();
        SetStatus(applied.Added ? "FmsBroadcastAdded" : "FmsBroadcastUpdated", applied.Channel.Title);
        return applied.Channel;
    }

    // The casts this window started, by broadcast id: what a cast-stop is allowed to end. A stop names the
    // broadcast (DEVICE-EXCHANGE 7.8); one that matches no entry here is not ours to act on, so it can never
    // close a window or silence a station the user chose themselves.
    private const int MaximumTrackedCasts = 16;
    private readonly Dictionary<string, ActiveCast> _activeCasts = new(StringComparer.Ordinal);

    private sealed record ActiveCast(string BroadcastId, string? CastId, Guid ChannelId);

    /// <summary>
    /// The question for one cast offer. It runs off the service's read loop, so it may stay open for the whole
    /// offer window; the token ends it when the window runs out or the session ends, and the dialog goes
    /// with it. The wait for the dialog never outlives the token, whether or not the dialog could be closed.
    /// </summary>
    private async Task<bool> PromptCastOfferAsync(ExchangeCastOffer offer, CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var shown = Dispatcher.InvokeAsync(() => ShowCastOfferPrompt(offer, cancellationToken));
        return await shown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool ShowCastOfferPrompt(ExchangeCastOffer offer, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var senderDevice = !string.IsNullOrWhiteSpace(offer.DeviceName) ? offer.DeviceName : offer.DeviceId;
        var broadcastTitle = !string.IsNullOrWhiteSpace(offer.Title) ? offer.Title : "Live Broadcast";

        string? currentlyPlaying = null;
        if (_playerWindows.Count > 0)
        {
            currentlyPlaying = _playerWindows.First().Channel.Title;
        }
        else if (_playingAudio is not null)
        {
            currentlyPlaying = _playingAudio.DisplayTitle ?? _playingAudio.Channel.Title;
        }

        var promptText = !string.IsNullOrEmpty(currentlyPlaying)
            ? LocalizationService.Format("ExchangeCastOfferPromptInterrupt", broadcastTitle, senderDevice, currentlyPlaying)
            : LocalizationService.Format("ExchangeCastOfferPrompt", broadcastTitle, senderDevice);

        var owner = DialogOwner;
        var open = true;
        using var dismissal = cancellationToken.Register(() => Dispatcher.BeginInvoke(() =>
        {
            // Both this flag and the dialog live on the UI thread, so a dismissal that arrives after the
            // user's own answer finds the flag down and touches nothing.
            if (open)
            {
                PromptDismissal.Close(owner);
            }
        }));
        try
        {
            // No is the default: a stray Enter or Space while the user types must not accept a cast that
            // interrupts their playback.
            var result = MessageBox.Show(
                owner,
                promptText,
                LocalizationService.Get("ExchangeCastTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            return result == MessageBoxResult.Yes;
        }
        finally
        {
            open = false;
        }
    }

    private void PlayCastOffer(ExchangeCastOffer offer)
    {
        if (Dispatcher.HasShutdownStarted || offer.Descriptor is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                FastMediaSorterBroadcastApplyResult? applied = null;
                await PersistAsync(state =>
                {
                    applied = FastMediaSorterBroadcastImport.Apply(
                        state.Channels, offer.Descriptor, DateTimeOffset.UtcNow, offer.BroadcastId);
                    return state with { Channels = [.. applied.Channels] };
                });

                if (applied is null)
                {
                    return;
                }

                if (_activeCasts.Count >= MaximumTrackedCasts)
                {
                    _activeCasts.Clear();
                }

                var channel = applied.Channel;
                _activeCasts[offer.BroadcastId] = new ActiveCast(offer.BroadcastId, offer.CastId, channel.Id);
                PopulateFacets();
                ApplyFilter();
                await StartCastPlaybackAsync(channel);
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(PlayCastOffer), exception);
            }
        });
    }

    /// <summary>
    /// Plays the cast's channel without the toggle a user's second click on a station means: PlayChannelAsync
    /// stops a station that is already playing, and a repeated or auto-accepted offer for the broadcast that is
    /// on must leave it on - the sender was told "accepted". An endpoint that changed restarts it cleanly.
    /// </summary>
    private async Task StartCastPlaybackAsync(StreamChannel channel)
    {
        if (_playerWindows.Any(window => window.Channel.Id == channel.Id))
        {
            return;
        }

        if (channel.MediaKind == MediaKind.Audio && _playingAudio is { } playing && playing.Channel.Id == channel.Id)
        {
            if (string.Equals(playing.Channel.Url, channel.Url, StringComparison.Ordinal))
            {
                return;
            }

            // The internal stop keeps the sleep timer, as a station switch does; the play below then finds
            // nothing playing and starts the new endpoint.
            StopAudioPlayback();
        }

        await PlayChannelAsync(channel, rememberSelection: false);
    }

    private void StopCast(ExchangeCastStop stop)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var cast = stop.BroadcastId is { } broadcastId
                    ? _activeCasts.GetValueOrDefault(broadcastId)
                    : stop.CastId is { } castId
                        ? _activeCasts.Values.FirstOrDefault(item => item.CastId == castId)
                        : null;
                if (cast is null)
                {
                    return;
                }

                _activeCasts.Remove(cast.BroadcastId);
                var windows = _playerWindows.Where(window => window.Channel.Id == cast.ChannelId).ToArray();
                var audioPlaying = _playingAudio?.Channel.Id == cast.ChannelId;
                if (windows.Length == 0 && !audioPlaying)
                {
                    return;
                }

                foreach (var window in windows)
                {
                    window.Close();
                }

                if (audioPlaying)
                {
                    StopAudio();
                }

                SetStatus("ExchangeCastStopped");
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(StopCast), exception);
            }
        });
    }
}

/// <summary>
/// Closes the modal message box the cast prompt shows. A Yes/No box has no close button and ignores WM_CLOSE,
/// so the only way to take it down from code is to end its dialog with the answer it already defaults to.
/// </summary>
internal static class PromptDismissal
{
    private const uint GetWindowEnabledPopup = 6;
    private const int ClassNameBuffer = 16;
    private const int AnswerNo = 7;

    internal static void Close(Window owner)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle == IntPtr.Zero)
        {
            return;
        }

        // A modal owner is disabled and its one enabled popup is the box; anything else (a window class that
        // is not the system dialog class) is left alone.
        var popup = GetWindow(ownerHandle, GetWindowEnabledPopup);
        if (popup == IntPtr.Zero || popup == ownerHandle)
        {
            return;
        }

        var className = new StringBuilder(ClassNameBuffer);
        if (GetClassName(popup, className, className.Capacity) > 0
            && string.Equals(className.ToString(), "#32770", StringComparison.Ordinal))
        {
            EndDialog(popup, new IntPtr(AnswerNo));
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDialog(IntPtr dialog, IntPtr result);
}
