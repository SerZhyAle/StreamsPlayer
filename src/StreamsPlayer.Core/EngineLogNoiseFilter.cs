using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Collapses repeated media-engine log records to one line per distinct message shape per window,
/// carrying the suppressed count. Evidence: the owner's log archive of 2026-09-06, the same one that
/// produced SP-0096 and SP-0097.
/// </summary>
/// <remarks>
/// <para>
/// SP-0097 reduced the periodic STATS volume and suppressed one known-harmless engine message by name.
/// The owner's archive of 2026-09-06 shows why a named suppression cannot be the whole answer: the VLC
/// records of one session (2083 lines) reduce to about twelve distinct message shapes, and an earlier
/// session in the same archive is 9291 VLC lines of which one pair of libdvbpsi messages accounts for
/// 4645 occurrences each - 80% of that log. Every one of those shapes was unknown when the named
/// suppression was written, and the next engine build will bring shapes unknown today.
/// </para>
/// <para>
/// What is suppressed is the <em>repetition</em>, never the fact: the first occurrence of a shape is
/// always written, and every record the filter swallowed is accounted for by a later count. SP-0134: the
/// count used to ride only on the next occurrence after the window, so the last burst of a session - the
/// one that usually explains why it was closed - never reported its size. A count is now also handed out
/// by <see cref="Flush"/> once a burst's window has run out, when its shape is evicted, and by
/// <see cref="FlushAll"/> when the engine goes away.
/// </para>
/// <para>
/// Not thread-safe by design, matching <see cref="PlaybackStatsFilter"/>: engine log callbacks arrive on
/// engine threads, and the caller that owns the log already owns a lock worth reusing.
/// </para>
/// </remarks>
public sealed class EngineLogNoiseFilter
{
    /// <summary>How long one message shape stays suppressed after it has been written.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Largest number of distinct shapes tracked at once. A ceiling rather than a trim target: the map is
    /// keyed by a normalized shape, so a healthy session holds a few dozen keys, and a hostile or simply
    /// unforeseen engine build that varies its wording without varying its digits must not be able to
    /// grow this without bound inside a long session.
    /// </summary>
    public const int MaximumTrackedShapes = 256;

    private readonly Dictionary<string, ShapeState> _shapes = new(StringComparer.Ordinal);

    // Counts of shapes evicted while they still owed one, held until the next Flush. Bounded by the
    // number of admissions between two flushes, and emptied by every flush.
    private readonly List<EngineLogRepeatSummary> _evicted = [];

    /// <summary>
    /// Decides whether this record is written, and how many of its kind were swallowed since the last
    /// one that was.
    /// </summary>
    /// <param name="module">The engine module that emitted the record.</param>
    /// <param name="message">The record's text, unnormalized.</param>
    /// <param name="currentTicks">A monotonic timestamp in the caller's ticks.</param>
    /// <param name="frequency">Ticks per second for <paramref name="currentTicks"/>.</param>
    /// <param name="level">The record's severity as the caller names it; carried into a later count.</param>
    public EngineLogSample Observe(string module, string? message, long currentTicks, long frequency, string? level = null)
    {
        var key = string.Concat(module, "\u0000", NormalizeShape(message));
        if (!_shapes.TryGetValue(key, out var state))
        {
            Admit(key, new ShapeState(currentTicks, 0, module, message, level));
            return new EngineLogSample(true, 0);
        }

        if (currentTicks - state.LastLoggedTicks < WindowTicks(frequency))
        {
            // The latest wording is kept for the count: it is the one nearest to when the burst ended.
            _shapes[key] = state with { Suppressed = state.Suppressed + 1, Message = message, Level = level };
            return new EngineLogSample(false, 0);
        }

        _shapes[key] = new ShapeState(currentTicks, 0, module, message, level);
        return new EngineLogSample(true, state.Suppressed);
    }

    /// <summary>
    /// Hands out every count that is due: shapes evicted since the last call, and shapes whose window has
    /// run out while they still owed a count. Call it periodically; a burst's size is then written within
    /// one window plus one period of the burst ending.
    /// </summary>
    /// <remarks>
    /// A shape that reports here starts a new window at <paramref name="currentTicks"/>, exactly as if its
    /// record had been written: a burst that is still running then costs one count per window, not a count
    /// plus a fresh record. A shape whose window ran out with nothing owed is forgotten, so the map holds
    /// only what is live.
    /// </remarks>
    public IReadOnlyList<EngineLogRepeatSummary> Flush(long currentTicks, long frequency)
    {
        var due = TakeEvicted();
        var windowTicks = WindowTicks(frequency);
        List<string>? expired = null;
        List<KeyValuePair<string, ShapeState>>? restarted = null;
        foreach (var entry in _shapes)
        {
            var state = entry.Value;
            if (currentTicks - state.LastLoggedTicks < windowTicks)
            {
                continue;
            }

            if (state.Suppressed == 0)
            {
                (expired ??= []).Add(entry.Key);
                continue;
            }

            due.Add(Summarize(state, EngineLogRepeatReason.WindowClosed));
            (restarted ??= []).Add(new(entry.Key, state with { LastLoggedTicks = currentTicks, Suppressed = 0 }));
        }

        expired?.ForEach(key => _shapes.Remove(key));
        restarted?.ForEach(entry => _shapes[entry.Key] = entry.Value);
        return due;
    }

    /// <summary>
    /// Hands out every count still owed, whatever its window, and forgets every shape. For the moment the
    /// engine's log goes away: whatever is not written now is never written.
    /// </summary>
    public IReadOnlyList<EngineLogRepeatSummary> FlushAll()
    {
        var due = TakeEvicted();
        foreach (var state in _shapes.Values)
        {
            if (state.Suppressed > 0)
            {
                due.Add(Summarize(state, EngineLogRepeatReason.Closed));
            }
        }

        _shapes.Clear();
        return due;
    }

    /// <summary>Forgets every shape, so the next record of each is written again.</summary>
    public void Reset()
    {
        _shapes.Clear();
        _evicted.Clear();
    }

    private void Admit(string key, ShapeState admitted)
    {
        if (_shapes.Count >= MaximumTrackedShapes)
        {
            // Evict the shape whose last written record is oldest: it is the one whose suppression is
            // closest to expiring anyway, so dropping it costs at most one duplicate line - and its count,
            // if it owes one, is kept for the next flush rather than dropped with it.
            var oldest = key;
            var oldestTicks = long.MaxValue;
            foreach (var (candidate, state) in _shapes)
            {
                if (state.LastLoggedTicks < oldestTicks)
                {
                    oldestTicks = state.LastLoggedTicks;
                    oldest = candidate;
                }
            }

            if (_shapes.Remove(oldest, out var evicted) && evicted.Suppressed > 0)
            {
                _evicted.Add(Summarize(evicted, EngineLogRepeatReason.Evicted));
            }
        }

        _shapes[key] = admitted;
    }

    private List<EngineLogRepeatSummary> TakeEvicted()
    {
        var due = new List<EngineLogRepeatSummary>(_evicted);
        _evicted.Clear();
        return due;
    }

    private static long WindowTicks(long frequency) => (long)(RepeatWindow.TotalSeconds * frequency);

    private static EngineLogRepeatSummary Summarize(ShapeState state, EngineLogRepeatReason reason) =>
        new(state.Module, state.Level, state.Message ?? string.Empty, state.Suppressed, reason);

    /// <summary>
    /// Reduces a record to its shape by replacing every run of digits with <c>#</c>.
    /// </summary>
    /// <remarks>
    /// Digits only, deliberately. They are what varies between repetitions of one engine complaint - a
    /// stream number, a PID, a microsecond count, a hex status - while the words around them are the
    /// complaint itself. Normalizing anything wider would merge genuinely different failures into one
    /// suppressed shape, which is the one mistake this filter must not make.
    /// </remarks>
    public static string NormalizeShape(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(message.Length);
        var inDigits = false;
        foreach (var character in message)
        {
            if (char.IsAsciiDigit(character))
            {
                if (!inDigits)
                {
                    builder.Append('#');
                    inDigits = true;
                }

                continue;
            }

            inDigits = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    private readonly record struct ShapeState(long LastLoggedTicks, int Suppressed, string Module, string? Message, string? Level);
}

/// <summary>One filter decision: whether to write the record, and how many like it were swallowed.</summary>
public readonly record struct EngineLogSample(bool ShouldLog, int SuppressedRepeats);

/// <summary>Why a suppressed count was handed out without a record of its shape to carry it.</summary>
public enum EngineLogRepeatReason
{
    /// <summary>The shape's window ran out with records still unaccounted for.</summary>
    WindowClosed,

    /// <summary>The shape was evicted to admit a new one.</summary>
    Evicted,

    /// <summary>The engine's log went away.</summary>
    Closed,
}

/// <summary>A count of swallowed records, with the latest wording and severity of the shape they shared.</summary>
public readonly record struct EngineLogRepeatSummary(
    string Module,
    string? Level,
    string Message,
    int Repeats,
    EngineLogRepeatReason Reason);
