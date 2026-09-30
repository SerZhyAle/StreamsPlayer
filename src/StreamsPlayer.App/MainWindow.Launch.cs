using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    // SP-0118: a launch forwarded by a later copy can arrive while this window is still loading. Until
    // the startup launch has run, forwarded ones wait here, so they neither race the catalog load nor
    // get overtaken by the resume that an argument-free startup may perform.
    private readonly Queue<StreamLaunchRequest> _pendingForwardedLaunches = new();
    private bool _startupLaunchHandled;

    private async Task StartRequestedPlaybackAsync()
    {
        try
        {
            // SP-0062: taken before the switch, because nothing is playing yet whichever branch runs. An
            // explicit launch consumes the record without replaying it, so the stream the user named is the
            // only thing this session goes on to remember.
            var recorded = await TakeRecordedPlaybackAsync();
            if (!await PlayLaunchTargetAsync(_launchRequest))
            {
                // SP-0008 played the last selected channel here unconditionally, and the selection is
                // written by merely highlighting a row - so an ordinary launch could stream a channel the
                // user had never listened to, with no way to stop it. SP-0062 withdraws that: an
                // argument-free launch now starts nothing unless the user asked for a resume, and what it
                // resumes is what was really playing. The absence of automatic playback here is a decision.
                await ResumeRecordedPlaybackAsync(recorded);
            }
        }
        finally
        {
            // SP-0170: also when the startup launch threw - the exception still propagates to the caller's
            // boundary, but the launches that arrived meanwhile are not left waiting behind it.
            await DrainForwardedLaunchesAsync();
        }
    }

    /// <summary>
    /// SP-0170: opens the gate forwarded launches wait at and plays the ones queued behind it, each on its own -
    /// one that throws is reported and the rest still play. Safe to call again: the queue is simply empty then.
    /// Called from <see cref="StartRequestedPlaybackAsync"/> and, as the last resort for a start-up that never
    /// got that far, from the end of <c>MainWindow_Loaded</c>.
    /// </summary>
    private async Task DrainForwardedLaunchesAsync()
    {
        _startupLaunchHandled = true;
        while (!_shuttingDown && _pendingForwardedLaunches.TryDequeue(out var forwarded))
        {
            try
            {
                await PlayLaunchTargetAsync(forwarded);
            }
            catch (Exception exception)
            {
                _log.Error("Queued forwarded launch failed", exception);
            }
        }
    }

    /// <summary>
    /// SP-0118: a launch another copy of the application forwarded before exiting. It is acted on exactly
    /// as a command line of this process would be - except that an argument-free one never resumes
    /// anything, because this session is already running - and the application comes to the front.
    /// </summary>
    internal async void ReceiveForwardedLaunch(StreamLaunchRequest request)
    {
        try
        {
            // SP-0170: the listener stops accepting as the close begins, so this is only a launch that was
            // already on its way. Nothing is brought to the front or played by a window that is closing.
            if (_shuttingDown)
            {
                _log.Event("LAUNCH FORWARDED", $"kind={request.Kind}", "result=dropped_closing");
                return;
            }

            var foreground = BringApplicationToFront();
            _log.Event("LAUNCH FORWARDED", $"kind={request.Kind}", $"foreground={foreground}");
            if (!_startupLaunchHandled)
            {
                _pendingForwardedLaunches.Enqueue(request);
                return;
            }

            await PlayLaunchTargetAsync(request);
        }
        catch (Exception exception)
        {
            _log.Error("Forwarded launch failed", exception);
        }
    }

    /// <summary>
    /// Acts on an explicit launch target; <see langword="false"/> for <see cref="StreamLaunchTargetKind.None"/>,
    /// which names nothing to play.
    /// </summary>
    private async Task<bool> PlayLaunchTargetAsync(StreamLaunchRequest request)
    {
        switch (request.Kind)
        {
            case StreamLaunchTargetKind.Url:
                await PlayChannelAsync(CreateExternalChannel(request.Url!), rememberSelection: false);
                return true;
            case StreamLaunchTargetKind.ChannelId:
                // SP-0127: a shortcut carries the channel's address beside its id, so a row a refresh
                // replaced under a new id is found again by address - and an address no row holds any
                // more still plays, exactly as a --url launch of it would.
                var requestedChannel = StreamLaunchArguments.Resolve(_state.Channels, request);
                if (requestedChannel is null && request.Url is { } fallbackUrl)
                {
                    _log.Event("LAUNCH", "op=resolve", "result=address_only");
                    requestedChannel = CreateExternalChannel(fallbackUrl);
                }
                else if (requestedChannel is not null && requestedChannel.Id != request.ChannelId)
                {
                    _log.Event("LAUNCH", "op=resolve", "result=by_address");
                }

                if (requestedChannel is null)
                {
                    SetStatus("LaunchChannelNotFound");
                    return true;
                }

                await PlayChannelAsync(requestedChannel, rememberSelection: false);
                return true;
            case StreamLaunchTargetKind.Invalid:
                SetStatus("LaunchArgumentsInvalid");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// SP-0118: brings the surface the user is working with to the front - the compact panel while the
    /// catalog is hidden behind it (SP-0080 keeps exactly one of the two on screen), otherwise this
    /// window, restored first when minimized. Whichever monitor it is on, it stays there.
    /// </summary>
    private string BringApplicationToFront()
    {
        if (_compactPanel is { } panel)
        {
            if (panel.WindowState == WindowState.Minimized)
            {
                panel.WindowState = WindowState.Normal;
            }

            panel.Activate();
            return ForegroundActivation.Bring(panel);
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            SystemCommands.RestoreWindow(this);
        }

        Activate();
        return ForegroundActivation.Bring(this);
    }

    // SP-0067: this fires on every card click, and it used to serialize the entire channel catalog -
    // 15.15 MB and up to 377 ms on the owner's 19 855 channels - to record which row was highlighted.
    // The id lives in the browsing session now. Nothing reads it back to start a stream: SP-0062
    // withdrew play-on-launch, so this restores a highlight and nothing more.
    private async Task RememberSelectedChannelAsync(Guid channelId)
    {
        if (_session.LastSelectedChannelId == channelId || _state.Channels.All(channel => channel.Id != channelId))
        {
            return;
        }

        _session = _session with { LastSelectedChannelId = channelId };
        await PersistSessionAsync();
    }

    /// <summary>
    /// SP-0124: the one refusal every launch path shares, taken before any engine exists. A row whose
    /// address is not <c>http</c>, <c>https</c> or <c>rtsp</c> - or does not parse - reaches the user as the
    /// ordinary unavailable-channel outcome; handed to an engine it ended the process, or opened a network
    /// share with the user's credentials. <see langword="true"/> when the channel was refused.
    /// </summary>
    private async Task<bool> RefuseUnlaunchableAsync(StreamChannel channel, bool quiet = false, bool randomHunt = false)
    {
        if (LaunchableAddress.IsLaunchable(channel.Url))
        {
            return false;
        }

        _log.Event("REFUSE", "op=playback", "reason=not_launchable", $"kind={channel.MediaKind}", $"url={channel.Url}");
        if (randomHunt)
        {
            // The draw already excludes these rows; a hunt that meets one anyway moves on at once.
            YieldToRandomStationHunt(channel, "not_launchable");
            return true;
        }

        if (quiet)
        {
            SetStatus("ResumeStreamFailed");
            return true;
        }

        var displayTitle = StreamTitleFormatter.Display(channel.Title);
        if (IsCompact)
        {
            // SP-0080: see IsCompact - a modal owned by the hidden catalog would sit under the panel.
            SetStatus("PlaybackAddressNotLaunchable", displayTitle);
            return true;
        }

        var report = FailureReportFormatter.Format(new FailureReport(
            ProductInfo.Version,
            DateTimeOffset.UtcNow,
            channel.Title,
            channel.Url,
            channel.MediaKind,
            PlaybackErrorCategory.Unsupported));
        var dialog = new PlaybackFailureDialog(
            channel.Title,
            channel.SourceOrigin,
            report,
            channel.Access,
            LocalizationService.Format("PlaybackAddressNotLaunchable", displayTitle),
            canRetry: false) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Choice == PlaybackFailureChoice.Remove)
        {
            await RemoveChannelAsync(channel);
        }

        return true;
    }

    private static StreamChannel CreateExternalChannel(string url)
    {
        // StreamLaunchRequest.Parse admits only a launchable address, so the host is always there; the
        // fallback keeps this total rather than trusting that from a distance.
        return new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = LaunchableAddress.TryParse(url, out var address) ? address.Host : url,
            MediaKind = StreamMediaKindClassifier.Classify(url),
            SourceOrigin = SourceOrigin.Manual,
            SortIndex = 0,
            AddedAt = DateTimeOffset.UtcNow
        };
    }
}
