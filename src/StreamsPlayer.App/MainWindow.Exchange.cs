using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private ExchangeSourceService Exchange => ((App)Application.Current).Exchange;

    internal void InitializeExchangeStatus(ExchangeSourceService source)
    {
        void Refresh()
        {
            // Both buttons belong to the exchange server, so both follow the Settings page: an account that is
            // switched off or was never registered shows neither. The status line and the way back in stay
            // in Settings > Library, which is where a device without an active account is told why (SP-0201).
            var visibility = source.IsActive ? Visibility.Visible : Visibility.Collapsed;
            ExchangeSourceStatusButton.Visibility = visibility;
            BroadcastsButton.Visibility = visibility;
            ExchangeSourceStatusText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, source.StatusKey);
            if (source.SignedOutNotice)
            {
                // SP-0221 requirement 7: the account ended this sign-in. One message that says which, then the
                // prompt with the server and login kept; nothing retries in the background. Shown once.
                source.AcknowledgeSignedOutNotice();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                    HandlerBoundary.Run(nameof(InitializeExchangeStatus), () =>
                    {
                        MessageBox.Show(this, LocalizationService.Get("ExchangeSignedOut") + Environment.NewLine
                            + LocalizationService.Get("ExchangeSignInAgain"), LocalizationService.Get("ExchangeTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        if (!_busy)
                        {
                            OpenSettings(1);
                        }

                        return Task.CompletedTask;
                    }));
            }
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
        => OpenExchangeBroadcasts(DialogOwner);

    // SP-0222: the list is a companion surface (APP-BEHAVIOUR rule 1, widened 0.10): it stays open beside the
    // windows it opens, so it is not modal, and there is one of it - a second open brings the first forward.
    private BroadcastsWindow? _broadcastsWindow;

    internal void OpenExchangeBroadcasts(Window owner)
    {
        try
        {
            if (_broadcastsWindow is { } open)
            {
                ActivateWindow(open);
                return;
            }

            var window = new BroadcastsWindow(this, Exchange) { Owner = owner };
            _broadcastsWindow = window;
            window.Closed += (_, _) => _broadcastsWindow = null;
            window.Show();
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
                // A user operation owns the window, so this frame's mutation is not run against a state
                // the operation may replace. SP-0212 (L13): it is not dropped either - a directory at
                // rest may never send another frame - so it is remembered and applied when the operation
                // releases the window, against the latest snapshot, exactly as a fresh frame would.
                _exchangeDirectoryPending = true;
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

    /// <summary>The view's Keep: the same import a link runs, then the reveal of the row it made.</summary>
    internal async Task KeepExchangeBroadcastAsync(ExchangeBroadcastView view)
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
                ? ExchangeFailureRows.FormatUnsupported.MessageKey : "FmsBroadcastUnsupportedMode");
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

    // The casts this window started: what a cast-stop is allowed to end. A stop names the broadcast
    // (DEVICE-EXCHANGE 7.8); one that matches no entry is not ours to act on, so it can never close a window
    // or silence a station the user chose themselves. The rules of the list live in Core, where they are tested.
    private readonly ExchangeCastLedger _activeCasts = new();

    private bool IsChannelPlaying(Guid channelId) =>
        _playingAudio?.Channel.Id == channelId || _playerWindows.Any(window => window.Channel.Id == channelId);

    /// <summary>Ends whatever plays the channel; false when nothing did.</summary>
    private bool EndCastPlayback(Guid channelId)
    {
        var windows = _playerWindows.Where(window => window.Channel.Id == channelId).ToArray();
        var audioPlaying = _playingAudio?.Channel.Id == channelId;
        if (windows.Length == 0 && !audioPlaying)
        {
            return false;
        }

        foreach (var window in windows)
        {
            window.Close();
        }

        if (audioPlaying)
        {
            StopAudio();
        }

        return true;
    }

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

        try
        {
            var shown = Dispatcher.InvokeAsync(() => ShowCastOfferPrompt(offer, cancellationToken));
            return await shown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The box or the dispatcher failing is this window's fault to report, and no reason to leave the
            // caster unanswered: the offer reads as declined. Only the exception type reaches the log
            // line - the offer's title and sender are not part of it.
            HandlerBoundary.Report(nameof(PromptCastOfferAsync),
                new InvalidOperationException("Cast prompt failed: " + exception.GetType().Name), notifyUser: false);
            return false;
        }
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
        var caption = LocalizationService.Get("ExchangeCastTitle");
        // Taken on this thread immediately before the box exists: whatever this owner already shows is not
        // the prompt, however alike, and nothing can appear between here and Show on the UI thread.
        var existing = PromptDismissal.Snapshot(owner);
        var open = true;
        using var dismissal = cancellationToken.Register(() => Dispatcher.BeginInvoke(() =>
        {
            // Both this flag and the dialog live on the UI thread, so a dismissal that arrives after the
            // user's own answer finds the flag down and touches nothing.
            if (open)
            {
                PromptDismissal.Close(owner, caption, existing);
            }
        }));
        try
        {
            // No is the default: a stray Enter or Space while the user types must not accept a cast that
            // interrupts their playback.
            var result = MessageBox.Show(
                owner,
                promptText,
                caption,
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
            // Registered before the save: the whole-state write is long enough for the sender's stop to
            // arrive, and a stop that finds no entry is dropped while the playback then starts anyway.
            var cast = _activeCasts.Track(offer.BroadcastId, offer.CastId, IsChannelPlaying);
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
                    // Nothing was imported, so there is no cast to stop.
                    _activeCasts.Remove(cast);
                    return;
                }

                var channel = applied.Channel;
                cast.ChannelId = channel.Id;
                PopulateFacets();
                ApplyFilter();
                if (cast.StopRequested)
                {
                    // The stop arrived while the import was being saved: the row stays, nothing is started.
                    return;
                }

                await StartCastPlaybackAsync(channel);
                cast.Settled = true;
                if (cast.StopRequested)
                {
                    // The stop arrived while the playback was starting, before anything was there to end.
                    EndCastPlayback(channel.Id);
                }
            }
            catch (Exception exception)
            {
                _activeCasts.Remove(cast);
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
        var open = _playerWindows.Where(window => window.Channel.Id == channel.Id).ToArray();
        if (open.Length > 0)
        {
            // A window plays the endpoint it was opened with. One that still matches is left on; one the
            // re-offered descriptor has outdated is closed and the channel reopened below, as audio is.
            var stale = open.Where(window => !string.Equals(window.Channel.Url, channel.Url, StringComparison.Ordinal)).ToArray();
            foreach (var window in stale)
            {
                window.Close();
            }

            if (stale.Length < open.Length)
            {
                return;
            }
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
                // The ledger flags the entry as well as removing it: the accept that registered it may still be
                // saving its import or starting playback, and it reads the flag to know the cast is over.
                var cast = _activeCasts.Stop(stop);
                if (cast is null)
                {
                    return;
                }

                if (cast.ChannelId is { } channelId && EndCastPlayback(channelId))
                {
                    SetStatus("ExchangeCastStopped");
                }
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(StopCast), exception);
            }
        });
    }
}
