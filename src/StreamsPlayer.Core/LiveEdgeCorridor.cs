using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0078: how far behind the live edge the optional engine is asked to keep the picture, and how hard
/// it may be driven to get back there.
/// </summary>
/// <remarks>
/// Three durations rather than a corridor's two, because the engine's latency controller cannot be
/// described by the corridor alone. It measures the distance as content it has downloaded but not yet
/// shown, so <see cref="Buffer"/> is the ceiling on what it is able to observe; and its correction speed
/// is that distance divided by <see cref="Target"/>, so the same buffer decides how hard the correction
/// can get. A buffer at the target makes the rule inert, and a buffer far above it makes the rule loud.
/// <para>The type exists so that relationship is guarded by a test instead of by a live session. The
/// optional engine is rarely switched on, so field evidence for the rule will be thin; reproducing the
/// engine's own arithmetic in <see cref="PeakSpeed"/> makes an edit to one of the three numbers fail a
/// check rather than quietly turn a gradual correction into the queue discard at
/// <see cref="QueueFlushSpeed"/> - a visible jump, the one outcome the ticket rules out.</para>
/// <para>Platform-neutral on purpose: nothing here names the engine's types. The App hands these
/// durations over and reports what the engine did with them.</para>
/// </remarks>
/// <param name="Target">The distance at which the engine starts speeding up.</param>
/// <param name="Floor">The distance at which it returns to normal speed; the near wall of the corridor.</param>
/// <param name="Buffer">
/// How far ahead the engine may read, which is also the furthest behind it can ever measure itself to
/// be. The engine will not accept less than twice <see cref="Target"/> here, so a smaller number would
/// be a claim rather than a setting.
/// </param>
public readonly record struct LiveEdgeCorridor(TimeSpan Target, TimeSpan Floor, TimeSpan Buffer)
{
    /// <summary>
    /// The engine's slowest correction. It never nudges by less than this, whatever the excess asks for,
    /// which is why a corridor cannot be made gentler simply by narrowing it.
    /// </summary>
    public const double GentlestSpeed = 1.1;

    /// <summary>
    /// Where the engine stops correcting and starts discarding: past this speed it drops the queue
    /// instead of playing it out. A corridor that can reach it trades a slow catch-up for a visible jump.
    /// </summary>
    public const double QueueFlushSpeed = 4.0;

    /// <summary>
    /// What ships: hold the picture 6-10 s behind the edge, reading up to 20 s ahead. The owner chose it
    /// on 2026-08-08 over the neighbouring product's 4-20 s, whose numbers were measured on another
    /// platform and another engine, and chose to leave the buffer at the engine's own derivation rather
    /// than keep this engine's live buffer at the 15 s the default engine uses.
    /// </summary>
    public static readonly LiveEdgeCorridor Default =
        new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(20));

    /// <summary>
    /// The fastest this corridor can drive playback: the engine's speed rule at a full buffer, which is
    /// as far behind as it can ever observe itself to be.
    /// </summary>
    public double PeakSpeed =>
        Math.Max(Math.Round(Buffer / Target, 1, MidpointRounding.ToPositiveInfinity), GentlestSpeed);

    /// <summary>How a corridor is spelled everywhere it is logged, so two log lines can be compared.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"target_ms={(long)Target.TotalMilliseconds} floor_ms={(long)Floor.TotalMilliseconds} buffer_ms={(long)Buffer.TotalMilliseconds} peak_speed={PeakSpeed:0.0#}");
}
