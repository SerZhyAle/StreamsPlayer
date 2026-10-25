using System.Net;

namespace StreamsPlayer.Core;

/// <summary>
/// The brute-force damper of the broadcast's own HTTP server: each refused credentialed attempt from one source address
/// costs that source a longer wait for the 401, up to a second, and a right answer clears the account. The caller
/// waits asynchronously, so no other connection is held up. The table is bounded and forgets a quiet source.
/// </summary>
/// <remarks>
/// Every tunnel client arrives from loopback, so they share one source here; the relay rate-limits them per its own
/// source before they reach this server. A source that never fails costs nothing.
/// </remarks>
internal sealed class BroadcastAttemptDamper(
    TimeProvider? timeProvider = null, int capacity = 256, TimeSpan? step = null, TimeSpan? ceiling = null)
{
    internal static readonly TimeSpan DefaultStep = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan Forget = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly TimeSpan _step = step ?? DefaultStep;
    private readonly TimeSpan _ceiling = ceiling ?? DefaultCeiling;
    private readonly Dictionary<IPAddress, Source> _sources = [];
    private readonly object _gate = new();

    /// <summary>Records a refused attempt and returns how long its 401 is to be held back.</summary>
    public TimeSpan RegisterFailure(IPAddress? source)
    {
        if (source is null)
        {
            return _ceiling;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var failures = _sources.TryGetValue(source, out var known) && now - known.Last <= Forget ? known.Failures + 1 : 1;
            if (!_sources.ContainsKey(source) && _sources.Count >= _capacity)
            {
                MakeRoom(now);
            }

            _sources[source] = new Source(failures, now);
            return TimeSpan.FromTicks(Math.Min(_ceiling.Ticks, _step.Ticks * failures));
        }
    }

    public void RegisterSuccess(IPAddress? source)
    {
        if (source is null)
        {
            return;
        }

        lock (_gate)
        {
            _sources.Remove(source);
        }
    }

    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var quiet in _sources.Where(entry => now - entry.Value.Last > Forget).Select(entry => entry.Key).ToList())
        {
            _sources.Remove(quiet);
        }

        if (_sources.Count >= _capacity)
        {
            _sources.Remove(_sources.MinBy(entry => entry.Value.Last).Key);
        }
    }

    private readonly record struct Source(int Failures, DateTimeOffset Last);
}
