namespace StreamsPlayer.App;

public partial class PlayerWindow
{
    /// <summary>
    /// SP-0222: what the broadcast list shows for a video broadcast opened in this window - the only live state
    /// such a broadcast has, read from the window's own flags and never from the network. Read on the UI thread.
    /// </summary>
    internal BroadcastLegState BroadcastState =>
        _closing ? BroadcastLegState.Stopped
        : _failureShown ? BroadcastLegState.Failed
        : _recoveryInFlight ? BroadcastLegState.Reconnecting
        : _reachedLive ? BroadcastLegState.Playing
        : BroadcastLegState.Connecting;
}
