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
/// always written, and after the window the next occurrence is written with the number of records the
/// filter swallowed. A reader therefore still sees that the stream lost pictures a thousand times; it
/// costs one line rather than a thousand.
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

    /// <summary>
    /// Decides whether this record is written, and how many of its kind were swallowed since the last
    /// one that was.
    /// </summary>
    /// <param name="module">The engine module that emitted the record.</param>
    /// <param name="message">The record's text, unnormalized.</param>
    /// <param name="currentTicks">A monotonic timestamp in the caller's ticks.</param>
    /// <param name="frequency">Ticks per second for <paramref name="currentTicks"/>.</param>
    public EngineLogSample Observe(string module, string? message, long currentTicks, long frequency)
    {
        var key = string.Concat(module, "\u0000", NormalizeShape(message));
        if (!_shapes.TryGetValue(key, out var state))
        {
            Admit(key, currentTicks);
            return new EngineLogSample(true, 0);
        }

        var windowTicks = (long)(RepeatWindow.TotalSeconds * frequency);
        if (currentTicks - state.LastLoggedTicks < windowTicks)
        {
            _shapes[key] = state with { Suppressed = state.Suppressed + 1 };
            return new EngineLogSample(false, 0);
        }

        _shapes[key] = new ShapeState(currentTicks, 0);
        return new EngineLogSample(true, state.Suppressed);
    }

    /// <summary>Forgets every shape, so the next record of each is written again.</summary>
    public void Reset() => _shapes.Clear();

    private void Admit(string key, long currentTicks)
    {
        if (_shapes.Count >= MaximumTrackedShapes)
        {
            // Evict the shape whose last written record is oldest: it is the one whose suppression is
            // closest to expiring anyway, so dropping it costs at most one duplicate line.
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

            _shapes.Remove(oldest);
        }

        _shapes[key] = new ShapeState(currentTicks, 0);
    }

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

    private readonly record struct ShapeState(long LastLoggedTicks, int Suppressed);
}

/// <summary>One filter decision: whether to write the record, and how many like it were swallowed.</summary>
public readonly record struct EngineLogSample(bool ShouldLog, int SuppressedRepeats);
