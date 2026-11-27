namespace StreamsPlayer.Core;

/// <summary>STRWIN-0303: the time window of <c>REMOTE-CONTROL</c> rule 12.</summary>
public static class RemoteCommandWindow
{
    /// <summary>The clock difference two devices of one person are allowed.</summary>
    public const int ClockAllowance = 300;

    public static TimeSpan Allowance { get; } = TimeSpan.FromSeconds(ClockAllowance);

    /// <summary>The moment from which the time check refuses the request anyway, and until which the ring keeps it.</summary>
    public static DateTimeOffset KeepUntil(DateTimeOffset issuedAt, int ttlSeconds) =>
        issuedAt + TimeSpan.FromSeconds(ttlSeconds) + Allowance;

    /// <summary>
    /// <c>expired</c>: <c>issuedAt + ttlSeconds + 300 s</c> is earlier than <paramref name="now"/>, or <c>issuedAt</c> is more
    /// than 300 s ahead of it. A request that waited behind a question is judged when its turn comes.
    /// </summary>
    public static bool IsExpired(DateTimeOffset issuedAt, int ttlSeconds, DateTimeOffset now) =>
        KeepUntil(issuedAt, ttlSeconds) < now || issuedAt > now + Allowance;
}

/// <summary>One handled request in the ring: who asked, its id, how long it is kept, and the answer bytes given.</summary>
public sealed record RemoteRingEntry(string ControllerId, string RequestId, DateTimeOffset KeepUntil, string Answer);

/// <summary>
/// STRWIN-0303: the ring of <c>REMOTE-CONTROL</c> rule 12 - every handled request per controller with the answer it got,
/// kept on disk until its own window has closed, so a repeated box is answered with the stored answer and never run
/// twice, also across a restart. An entry is never dropped before <see cref="RemoteRingEntry.KeepUntil"/>; when the
/// ring is full a new request is answered <c>busy</c> and not run, which loses nothing and repeats nothing.
/// </summary>
public sealed class RemoteCommandRing
{
    /// <summary>The bound on held entries. A ticked controller would need about three requests a second for twenty minutes.</summary>
    public const int MaximumEntries = 4096;

    private readonly Dictionary<(string Controller, string Request), RemoteRingEntry> _entries = [];

    public RemoteCommandRing(IEnumerable<RemoteRingEntry>? entries = null)
    {
        foreach (var entry in entries ?? [])
        {
            if (entry is { ControllerId.Length: > 0, RequestId.Length: > 0, Answer: not null })
            {
                _entries[(entry.ControllerId, entry.RequestId)] = entry;
            }
        }
    }

    public int Count => _entries.Count;

    public bool IsFull => _entries.Count >= MaximumEntries;

    public IReadOnlyList<RemoteRingEntry> Entries => _entries.Values.OrderBy(entry => entry.KeepUntil).ToArray();

    /// <summary>The answer already given to <paramref name="requestId"/> of <paramref name="controllerId"/>, if any.</summary>
    public string? AnswerOf(string controllerId, string requestId) =>
        _entries.TryGetValue((controllerId, requestId), out var entry) ? entry.Answer : null;

    public void Record(string controllerId, string requestId, DateTimeOffset keepUntil, string answer) =>
        _entries[(controllerId, requestId)] = new RemoteRingEntry(controllerId, requestId, keepUntil, answer);

    /// <summary>Drops the entries whose window has closed; true when one was dropped.</summary>
    public bool Prune(DateTimeOffset now)
    {
        var closed = _entries.Where(pair => pair.Value.KeepUntil < now).Select(pair => pair.Key).ToArray();
        foreach (var key in closed)
        {
            _entries.Remove(key);
        }

        return closed.Length > 0;
    }
}
