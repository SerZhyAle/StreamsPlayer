namespace StreamsPlayer.Core;

/// <summary>The identity of one playback connection, stamped into every event that connection raises.</summary>
/// <remarks>The default value is never current, so an event that was never stamped cannot pass for a live one.</remarks>
public readonly record struct PlaybackConnectionId(long Value);

/// <summary>
/// SP-0120: tells an event from the connection playing now apart from one raised by a connection that has since
/// been replaced or stopped.
/// </summary>
/// <remarks>
/// An engine raises its events on its own threads and the player handles them later, on the UI thread, by which
/// time the connection that raised one may be gone: a failure queued by the previous connection used to stop the
/// one that replaced it and spend its recovery budget a second time. The player stamps each connection when it
/// opens it and asks this sequence, where it handles the event, whether that connection is still the one playing.
/// <para>An identity is never issued twice, so a connection that was stopped and a later connection can never be
/// mistaken for each other. Safe to call from any thread.</para>
/// </remarks>
public sealed class PlaybackConnectionSequence
{
    private readonly Lock _gate = new();
    private long _issued;
    private long _current;

    /// <summary>Starts a new connection; from here on only its events are current.</summary>
    public PlaybackConnectionId Open()
    {
        lock (_gate)
        {
            _current = ++_issued;
            return new PlaybackConnectionId(_current);
        }
    }

    /// <summary>Ends the current connection without starting another; no event is current afterwards.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _current = 0;
        }
    }

    /// <summary>Whether an event stamped with <paramref name="connection"/> comes from the connection playing now.</summary>
    public bool IsCurrent(PlaybackConnectionId connection)
    {
        lock (_gate)
        {
            return connection.Value != 0 && connection.Value == _current;
        }
    }
}
