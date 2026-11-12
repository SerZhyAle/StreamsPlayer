using System.Net.Sockets;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0250: why a window decided that the broadcast it plays has ended (<c>LIVE-BROADCAST</c> item T and item U,
/// <c>DEVICE-EXCHANGE</c> items DX and DY). The words of <see cref="BroadcastEndCauses.Name"/> are the <c>cause=</c> field
/// of the one diagnostic line a deliberate end writes.
/// </summary>
public enum BroadcastEndCause
{
    /// <summary>The account's directory removed the broadcast, read at the moment the stream stopped.</summary>
    Directory,

    /// <summary>The relay answered <c>410 Gone</c> (item DX).</summary>
    RelayGone,

    /// <summary>The relay answered <c>404</c> for a broadcast this window had played before (item S reads it as "not started" otherwise).</summary>
    RelayNotFoundAfterPlay,

    /// <summary>A tunnel <c>connect</c> was refused with <c>ended</c> (item DY).</summary>
    TunnelEnded,

    /// <summary>The producer's LAN port refused the reconnect.</summary>
    LanRefused,

    /// <summary>The producer's LAN server answered the reconnect <c>404</c>.</summary>
    LanNotFound,

    /// <summary>The producer's LAN server answered the reconnect <c>410</c>.</summary>
    LanGone,

    /// <summary>An RTCP BYE where an engine reports one. LibVLC 3.0 does not, so nothing raises this today.</summary>
    Bye,

    /// <summary>The bounded recovery ran out after the stream had ended; the window then says so.</summary>
    BudgetExhausted
}

public static class BroadcastEndCauses
{
    public static string Name(BroadcastEndCause cause) => cause switch
    {
        BroadcastEndCause.Directory => "directory",
        BroadcastEndCause.RelayGone => "relay-410",
        BroadcastEndCause.RelayNotFoundAfterPlay => "relay-404-after-play",
        BroadcastEndCause.TunnelEnded => "tunnel-ended",
        BroadcastEndCause.LanRefused => "lan-refused",
        BroadcastEndCause.LanNotFound => "lan-404",
        BroadcastEndCause.LanGone => "lan-410",
        BroadcastEndCause.Bye => "bye",
        _ => "budget-exhausted"
    };
}

/// <summary>What one attempt of a reconnect was answered, as far as it tells an end from a lost connection.</summary>
public enum BroadcastEndAnswer
{
    /// <summary>No answer that says anything: a reset, a timeout, a refusal that is no end, or success.</summary>
    None,
    LanRefused,
    LanNotFound,
    LanGone,
    RelayGone,
    RelayNotFound,

    /// <summary>The relay's <c>503</c>: the upload stalled and the record stays, so this is no end (item DX).</summary>
    RelayStalled,
    TunnelEnded
}

public static class BroadcastEndAnswers
{
    /// <summary>The reasons the LAN attempts raise. They are reasons, not wire words, so they cannot be mistaken for a relay's.</summary>
    public const string LanRefusedReason = "lan-refused";
    public const string LanNotFoundReason = "lan-404";
    public const string LanGoneReason = "lan-410";

    /// <summary>The tunnel's refusal reason of item DY, as the forwarder passes it on.</summary>
    public const string TunnelEndedReason = "ended";

    /// <summary>Reads the reasons the transports already raise (the relay proxy's <c>relay-refused-NNN</c>, a tunnel's refusal, the LAN reasons above).</summary>
    public static BroadcastEndAnswer FromReason(string? reason) => reason switch
    {
        LanRefusedReason => BroadcastEndAnswer.LanRefused,
        LanNotFoundReason => BroadcastEndAnswer.LanNotFound,
        LanGoneReason => BroadcastEndAnswer.LanGone,
        // The Digest proxy of a protected LAN broadcast passes the producer's status on under its own reason.
        "broadcast-http-refused-404" => BroadcastEndAnswer.LanNotFound,
        "broadcast-http-refused-410" => BroadcastEndAnswer.LanGone,
        "relay-refused-410" => BroadcastEndAnswer.RelayGone,
        "relay-refused-404" => BroadcastEndAnswer.RelayNotFound,
        "relay-refused-503" => BroadcastEndAnswer.RelayStalled,
        TunnelEndedReason => BroadcastEndAnswer.TunnelEnded,
        _ => BroadcastEndAnswer.None
    };

    /// <summary>The reason string an answer is carried under; null for an answer that carries none.</summary>
    public static string? ReasonOf(BroadcastEndAnswer answer) => answer switch
    {
        BroadcastEndAnswer.LanRefused => LanRefusedReason,
        BroadcastEndAnswer.LanNotFound => LanNotFoundReason,
        BroadcastEndAnswer.LanGone => LanGoneReason,
        BroadcastEndAnswer.RelayGone => "relay-refused-410",
        BroadcastEndAnswer.RelayNotFound => "relay-refused-404",
        BroadcastEndAnswer.RelayStalled => "relay-refused-503",
        BroadcastEndAnswer.TunnelEnded => TunnelEndedReason,
        _ => null
    };

    /// <summary>
    /// A LAN HTTP attempt's outcome: the status it was answered, or the failure that stood in place of one. A refused
    /// connection is the port closed (item T); a reset, a timeout or any other failure says nothing.
    /// </summary>
    public static BroadcastEndAnswer FromLanHttp(int? status, Exception? error)
    {
        if (status == 404)
        {
            return BroadcastEndAnswer.LanNotFound;
        }

        if (status == 410)
        {
            return BroadcastEndAnswer.LanGone;
        }

        for (var inner = error; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return BroadcastEndAnswer.LanRefused;
            }
        }

        return BroadcastEndAnswer.None;
    }
}

/// <summary>What the watch says after an event: keep the bounded recovery going, or the broadcast ended.</summary>
public readonly record struct BroadcastEndVerdict(bool IsEnd, BroadcastEndCause Cause)
{
    public static BroadcastEndVerdict Continue => default;

    public static BroadcastEndVerdict End(BroadcastEndCause cause) => new(true, cause);
}

/// <summary>
/// SP-0250: the facts of one window's stream and the reading of an ended broadcast that <c>LIVE-BROADCAST</c> items T
/// and U ask of a consumer. The window tells it what happened; it answers whether the bounded recovery goes on.
/// <list type="bullet">
/// <item>A directory removal does not stop a stream that is still delivering; it is read when the stream stops, and ends
/// at once a stream that is stopped or being recovered.</item>
/// <item>A relay <c>410</c> and a tunnel <c>ended</c> end the broadcast whenever they come. A relay <c>404</c> and every LAN
/// answer end it only for a broadcast this window had played before: before that, <c>404</c> is "not started" (item S) and a
/// closed LAN port is a stale address.</item>
/// <item>A relay <c>503</c> (an upload stall) and every answer that says nothing end nothing.</item>
/// </list>
/// An end is sticky until <see cref="Reset"/> - the person's Reconnect. Safe from any thread.
/// </summary>
public sealed class BroadcastEndWatch
{
    private readonly object _gate = new();
    private bool _live;
    private bool _playedBefore;
    private bool _removalSeen;
    private BroadcastEndCause? _ended;

    public bool PlayedBefore
    {
        get { lock (_gate) return _playedBefore; }
    }

    /// <summary>The cause of the end this watch has decided, or null while the broadcast has not ended.</summary>
    public BroadcastEndCause? Ended
    {
        get { lock (_gate) return _ended; }
    }

    /// <summary>The stream is delivering. The first time after a stop it also drops a removal that was waiting (a re-announced broadcast is live again).</summary>
    public void NoteLive()
    {
        lock (_gate)
        {
            if (_ended is not null)
            {
                return;
            }

            if (!_live)
            {
                _live = true;
                _removalSeen = false;
            }

            _playedBefore = true;
        }
    }

    /// <summary>The stream stopped or failed to deliver: an end of body, a reset, a timeout, a stall.</summary>
    public BroadcastEndVerdict NoteStopped()
    {
        lock (_gate)
        {
            if (_ended is { } already)
            {
                return BroadcastEndVerdict.End(already);
            }

            _live = false;
            return _removalSeen ? Decide(BroadcastEndCause.Directory) : BroadcastEndVerdict.Continue;
        }
    }

    /// <summary>The directory removed the broadcast (or a fresh list no longer holds it).</summary>
    public BroadcastEndVerdict NoteRemoval()
    {
        lock (_gate)
        {
            if (_ended is { } already)
            {
                return BroadcastEndVerdict.End(already);
            }

            _removalSeen = true;
            return _live ? BroadcastEndVerdict.Continue : Decide(BroadcastEndCause.Directory);
        }
    }

    /// <summary>One attempt of a reconnect was answered.</summary>
    public BroadcastEndVerdict NoteAnswer(BroadcastEndAnswer answer)
    {
        lock (_gate)
        {
            if (_ended is { } already)
            {
                return BroadcastEndVerdict.End(already);
            }

            return CauseOf(answer) is { } cause ? Decide(cause) : BroadcastEndVerdict.Continue;
        }
    }

    /// <summary>Whether <paramref name="answer"/> would end the broadcast now; no state changes. The audio leg asks it to stop walking endpoints.</summary>
    public bool WouldEnd(BroadcastEndAnswer answer)
    {
        lock (_gate)
        {
            return _ended is not null || CauseOf(answer) is not null;
        }
    }

    /// <summary>The bounded recovery ran out after the stream had ended; the window shows the ended state with this cause.</summary>
    public BroadcastEndVerdict NoteBudgetExhausted()
    {
        lock (_gate)
        {
            return _ended is { } already ? BroadcastEndVerdict.End(already) : Decide(BroadcastEndCause.BudgetExhausted);
        }
    }

    /// <summary>The person's Reconnect: the window listens again. What it had played stays played.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _live = false;
            _removalSeen = false;
            _ended = null;
        }
    }

    private BroadcastEndCause? CauseOf(BroadcastEndAnswer answer) => answer switch
    {
        BroadcastEndAnswer.TunnelEnded => BroadcastEndCause.TunnelEnded,
        BroadcastEndAnswer.RelayGone => BroadcastEndCause.RelayGone,
        BroadcastEndAnswer.RelayNotFound when _playedBefore => BroadcastEndCause.RelayNotFoundAfterPlay,
        BroadcastEndAnswer.LanRefused when _playedBefore => BroadcastEndCause.LanRefused,
        BroadcastEndAnswer.LanNotFound when _playedBefore => BroadcastEndCause.LanNotFound,
        BroadcastEndAnswer.LanGone when _playedBefore => BroadcastEndCause.LanGone,
        _ => null
    };

    private BroadcastEndVerdict Decide(BroadcastEndCause cause)
    {
        _ended = cause;
        _live = false;
        return BroadcastEndVerdict.End(cause);
    }
}

/// <summary>
/// SP-0250 requirement 1: tells a window when the broadcast it plays leaves the account's directory. A removal is an
/// absence <em>after</em> a presence: the first list that never held the broadcast (one opened from the library, long
/// after it ended) is a baseline and says nothing, and a connection that is down (<c>null</c>) is unknown, not a removal.
/// After a lost control stream or an overflow the full list that follows is what this sees, so the absence of the played
/// broadcast in it is read as a removal. Safe from any thread.
/// </summary>
public sealed class BroadcastRemovalReader(string broadcastId)
{
    private readonly object _gate = new();
    private bool _wasPresent;

    public bool Observe(ExchangeDirectorySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }

        var present = snapshot.LiveBroadcastIds.Contains(broadcastId);
        lock (_gate)
        {
            if (present)
            {
                _wasPresent = true;
                return false;
            }

            if (!_wasPresent)
            {
                return false;
            }

            _wasPresent = false;
            return true;
        }
    }
}
