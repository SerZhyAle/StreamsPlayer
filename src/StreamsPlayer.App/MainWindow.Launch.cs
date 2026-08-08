using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class MainWindow
{
    private async Task StartRequestedPlaybackAsync()
    {
        // SP-0062: taken before the switch, because nothing is playing yet whichever branch runs. An
        // explicit launch consumes the record without replaying it, so the stream the user named is the
        // only thing this session goes on to remember.
        var recorded = await TakeRecordedPlaybackAsync();
        switch (_launchRequest.Kind)
        {
            case StreamLaunchTargetKind.Url:
                await PlayChannelAsync(CreateExternalChannel(_launchRequest.Url!), rememberSelection: false);
                break;
            case StreamLaunchTargetKind.ChannelId:
                var requestedChannel = _state.Channels.FirstOrDefault(channel => channel.Id == _launchRequest.ChannelId);
                if (requestedChannel is null)
                {
                    SetStatus("LaunchChannelNotFound");
                    return;
                }

                await PlayChannelAsync(requestedChannel, rememberSelection: false);
                break;
            case StreamLaunchTargetKind.Invalid:
                SetStatus("LaunchArgumentsInvalid");
                break;
            case StreamLaunchTargetKind.None:
                // SP-0008 played the last selected channel here unconditionally, and the selection is
                // written by merely highlighting a row - so an ordinary launch could stream a channel the
                // user had never listened to, with no way to stop it. SP-0062 withdraws that: an
                // argument-free launch now starts nothing unless the user asked for a resume, and what it
                // resumes is what was really playing. The absence of automatic playback here is a decision.
                await ResumeRecordedPlaybackAsync(recorded);
                break;
        }
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

    private static StreamChannel CreateExternalChannel(string url)
    {
        var uri = new Uri(url);
        return new StreamChannel
        {
            Id = Guid.NewGuid(),
            Url = url,
            Title = uri.Host,
            MediaKind = StreamMediaKindClassifier.Classify(url),
            SourceOrigin = SourceOrigin.Manual,
            SortIndex = 0,
            AddedAt = DateTimeOffset.UtcNow
        };
    }
}
