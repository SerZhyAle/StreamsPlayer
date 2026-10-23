using System.Collections.Concurrent;
using System.Threading.Channels;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0217: the producer side of the one control stream - the roles declared at enrollment,
/// the server facts `welcome` carries that an endpoint is built from, the outgoing
/// envelopes a broadcast's lifetime needs, the answers that come back, and the `open` of a tunnel the
/// server asks this device to bridge. The stream itself stays where it was: this partial only adds
/// what the producer posts and reads through it.
/// </summary>
internal sealed partial class ExchangeSourceService
{
    /// <summary>`welcome.publicEndpoint` - the host:port every relay and tunnel URL is built from; null while offline.</summary>
    internal string? PublicEndpoint { get; private set; }

    /// <summary>`welcome.features` - a client hides what is absent, so the descriptor carries `RELAY` and `TUNNEL` only when the server says it has them.</summary>
    internal IReadOnlyList<string> Features { get; private set; } = [];

    /// <summary>True once a <c>welcome</c> has been read: before that the server has not said what it lacks, and a client hides only what a list leaves out (7.2).</summary>
    internal bool FeaturesKnown { get; private set; }

    /// <summary>
    /// Whether this account's menus may offer a cast: only a member's devices cast (item AO), and not on a server
    /// whose feature list leaves <c>cast</c> out (7.2).
    /// </summary>
    internal bool CanCast => MayBroadcast && (!FeaturesKnown || Features.Contains("cast", StringComparer.Ordinal));

    /// <summary>Raised once per authenticated session, after `welcome`: the producer re-establishes its broadcasts (item H - publish again after every welcome).</summary>
    internal event Action? SessionOnline;

    /// <summary>Raised when the session ends, any way it ended. A lost connection is visible, never silent (constraint 6).</summary>
    internal event Action? SessionOffline;

    /// <summary>The answer to a cast this device sent (SP-0215's receiver side stays in the other events).</summary>
    internal event Action<ExchangeCastResult>? CastResult;

    /// <summary>The server asks this device to bridge a tunnel to its own broadcast (7.5): the handler owns the data stream and the byte pump.</summary>
    internal Action<ExchangeTunnelOpen, CancellationToken>? TunnelRequested;

    // One bounded queue of envelopes the producer posts while the read loop owns the stream: broadcast
    // start, update and end, cast and cast-stop. A write that cannot be handed over is refused at once,
    // never queued past the session it was meant for.
    private Channel<(object Envelope, TaskCompletionSource<bool> Written)>? _producerOutgoing;
    private readonly ConcurrentDictionary<string, PendingStart> _broadcastAnswers = [];
    private long _startOrder;
    private long _updateRefusalUntil;

    /// <summary>
    /// How long after a <c>broadcast-update</c> was posted its refusal can still arrive: the server answers an update
    /// with nothing but a refusal, so there is no answer to wait for, only a window in which one may come.
    /// </summary>
    private static readonly TimeSpan UpdateRefusalWindow = TimeSpan.FromSeconds(10);

    /// <summary>A <c>broadcast-start</c> awaiting its answer; <c>Order</c> is the order it was asked in, which is the order the server answers in.</summary>
    private sealed record PendingStart(long Order, TaskCompletionSource<string?> Answer);

    /// <summary>The product's roles at enroll (item AC); account kind and capture permission gate their use separately.</summary>
    internal string[] Roles() => ["receiver", "broadcaster"];

    /// <summary>
    /// Posts one envelope on the live control stream and completes when it was written. False when no
    /// session is up, the queue is full, or the write did not land - the caller owns the message.
    /// </summary>
    internal async Task<bool> PostControlEnvelopeAsync(object envelope, CancellationToken cancellationToken)
    {
        var outgoing = _producerOutgoing;
        if (outgoing is null)
        {
            return false;
        }

        var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!outgoing.Writer.TryWrite((envelope, written)))
        {
            return false;
        }

        try
        {
            return await written.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for the server's answer to one `broadcast-start`: null when it started, the refusal reason
    /// when it did not. The timeout ends the wait itself, so a silent server leaves no registration behind.
    /// </summary>
    internal async Task<string?> WaitForBroadcastStartAnswerAsync(string broadcastId, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingStart(Interlocked.Increment(ref _startOrder), answer);
        _broadcastAnswers[broadcastId] = pending;
        try
        {
            return await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _broadcastAnswers.TryRemove(new System.Collections.Generic.KeyValuePair<string, PendingStart>(broadcastId, pending));
        }
    }

    /// <summary>
    /// Posts a <c>broadcast-update</c> and opens the window in which its refusal may arrive, so the refusal is read as
    /// the update's and not as the end of the session (<see cref="TryAnswerBroadcastRefusal"/>).
    /// </summary>
    internal Task<bool> PostBroadcastUpdateAsync(object envelope, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _updateRefusalUntil, Environment.TickCount64 + (long)UpdateRefusalWindow.TotalMilliseconds);
        return PostControlEnvelopeAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// The reasons a server gives to a <c>broadcast-start</c> or <c>broadcast-update</c> it cannot take. The rest of
    /// section 7.10 is about the account, the device or the server as a whole, and keeps ending the session.
    /// </summary>
    internal static bool IsBroadcastRefusal(string? reason) =>
        reason is "capacity" or "limit-reached" or "id-collision" or "not-permitted" or "port-unavailable";

    /// <summary>
    /// 7.4 answers a start or an update with <c>refused</c> carrying the reason alone - every refusal in the server
    /// corpus is shaped so, and the broadcast is told by being the one in flight. The oldest start still waiting is
    /// the one the server answered; with none, a refusal inside an update's window is that update's (dropped, as an
    /// addressed one is). Anything else is not a broadcast's to take and returns false.
    /// </summary>
    private bool TryAnswerBroadcastRefusal(string? reason)
    {
        if (!IsBroadcastRefusal(reason))
        {
            return false;
        }

        var oldest = _broadcastAnswers.OrderBy(pair => pair.Value.Order).FirstOrDefault();
        if (oldest.Key is not null && _broadcastAnswers.TryRemove(oldest.Key, out var pending))
        {
            pending.Answer.TrySetResult(reason);
            return true;
        }

        return Environment.TickCount64 < Volatile.Read(ref _updateRefusalUntil);
    }

    private void RememberServerFacts(System.Text.Json.JsonElement welcome)
    {
        PublicEndpoint = ExchangeProtocol.String(welcome, "publicEndpoint");
        var features = new List<string>();
        if (welcome.TryGetProperty("features", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var feature in list.EnumerateArray())
            {
                if (feature.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                try
                {
                    if (feature.GetString() is { } name)
                    {
                        features.Add(name);
                    }
                }
                catch (InvalidOperationException)
                {
                    // A lone-surrogate escape is not a feature this client can act on.
                }
            }
        }

        Features = features;
        FeaturesKnown = true;
        SessionOnline?.Invoke();
    }

    private void AnswerBroadcastStart(System.Text.Json.JsonElement root, string? reason)
    {
        var broadcastId = ExchangeProtocol.String(root, "broadcastId");
        if (broadcastId is not null && _broadcastAnswers.TryRemove(broadcastId, out var pending))
        {
            pending.Answer.TrySetResult(reason);
        }
    }
}
