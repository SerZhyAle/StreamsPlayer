using StreamPlayer.Core;

namespace StreamPlayer.App;

public partial class MainWindow
{
    private async Task StartRequestedPlaybackAsync()
    {
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
                var lastSelectedChannel = _state.Channels.FirstOrDefault(channel => channel.Id == _state.LastSelectedChannelId);
                if (lastSelectedChannel is not null)
                {
                    await PlayChannelAsync(lastSelectedChannel, rememberSelection: false);
                }

                break;
        }
    }

    private async Task RememberSelectedChannelAsync(Guid channelId)
    {
        if (_state.LastSelectedChannelId == channelId || _state.Channels.All(channel => channel.Id != channelId))
        {
            return;
        }

        _state = await _store.SaveAsync(_state with { LastSelectedChannelId = channelId });
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
