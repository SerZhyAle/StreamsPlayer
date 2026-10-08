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

    private string? _activeCastId;
    private string? _activeCastBroadcastId;

    private async Task<bool> PromptCastOfferAsync(ExchangeCastOffer offer, CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return false;
        }

        return await Dispatcher.InvokeAsync(() =>
        {
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

            string promptText;
            if (!string.IsNullOrEmpty(currentlyPlaying))
            {
                promptText = LocalizationService.Format("ExchangeCastOfferPromptInterrupt", broadcastTitle, senderDevice, currentlyPlaying);
            }
            else
            {
                promptText = LocalizationService.Format("ExchangeCastOfferPrompt", broadcastTitle, senderDevice);
            }

            var result = MessageBox.Show(
                DialogOwner,
                promptText,
                LocalizationService.Get("ExchangeCastTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return result == MessageBoxResult.Yes;
        });
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
                _activeCastId = offer.CastId;
                _activeCastBroadcastId = offer.BroadcastId;

                FastMediaSorterBroadcastApplyResult? applied = null;
                await PersistAsync(state =>
                {
                    applied = FastMediaSorterBroadcastImport.Apply(
                        state.Channels, offer.Descriptor, DateTimeOffset.UtcNow, offer.BroadcastId);
                    return state with { Channels = [.. applied.Channels] };
                });

                if (applied is not null)
                {
                    PopulateFacets();
                    ApplyFilter();
                    await PlayChannelAsync(applied.Channel, rememberSelection: false);
                }
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(PlayCastOffer), exception);
            }
        });
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
                var matchesCast = (stop.CastId is not null && stop.CastId == _activeCastId)
                    || (stop.BroadcastId is not null && (stop.BroadcastId == _activeCastBroadcastId
                        || (_playingAudio?.Channel.FastMediaSorterBroadcast?.DirectoryBroadcastId == stop.BroadcastId)
                        || _playerWindows.Any(w => w.Channel.FastMediaSorterBroadcast?.DirectoryBroadcastId == stop.BroadcastId)));

                if (matchesCast)
                {
                    _activeCastId = null;
                    _activeCastBroadcastId = null;

                    foreach (var window in _playerWindows.ToArray())
                    {
                        if (stop.BroadcastId is null || window.Channel.FastMediaSorterBroadcast?.DirectoryBroadcastId == stop.BroadcastId)
                        {
                            window.Close();
                        }
                    }

                    if (_playingAudio?.Channel.FastMediaSorterBroadcast?.DirectoryBroadcastId == stop.BroadcastId || stop.BroadcastId is null)
                    {
                        StopAudio();
                    }

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
