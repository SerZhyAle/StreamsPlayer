using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// Throttles periodic playback STATS logging to delta-driven samples and periodic heartbeats (SP-0097).
/// </summary>
/// <remarks>
/// <para>
/// On a healthy stream, logging STATS every 2 s produces thousands of redundant records during long
/// sessions without conveying new diagnostic signal. This filter reduces steady-state volume by ~93%
/// while ensuring full 2 s resolution is immediately restored whenever loss, packet corruption, stream
/// discontinuity, or rate starvation is observed.
/// </para>
/// </remarks>
public sealed class PlaybackStatsFilter
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A rate below this reads <c>0.0</c> in the log, and is starvation. Kept equal to what the one-decimal
    /// log format rounds to zero, so what triggers a full-resolution sample is exactly what a reader of the
    /// log would call starved.
    /// </summary>
    public const double StarvedRate = 0.05;

    private long _lastLogTicks;
    private int _lastLostPics;
    private int _lastCorrupted;
    private int _lastDiscontinuity;
    private bool _hasLoggedForLeg;

    public void Reset()
    {
        _lastLogTicks = 0;
        _lastLostPics = 0;
        _lastCorrupted = 0;
        _lastDiscontinuity = 0;
        _hasLoggedForLeg = false;
    }

    /// <summary>
    /// Determines whether the sample should be emitted to the diagnostic log.
    /// </summary>
    public bool ShouldLog(
        string tag,
        int lostPictures,
        int corrupted,
        int discontinuity,
        double? inKbps,
        double? dispFps,
        long currentTicks,
        long frequency)
    {
        // Explicit events (e.g. STALL STATS, RESUME STATS) are always written.
        if (!string.Equals(tag, "STATS", StringComparison.Ordinal))
        {
            RecordLogged(lostPictures, corrupted, discontinuity, currentTicks);
            return true;
        }

        // First sample of a new playback leg is always written to establish the baseline.
        if (!_hasLoggedForLeg)
        {
            RecordLogged(lostPictures, corrupted, discontinuity, currentTicks);
            return true;
        }

        // Loss or corruption counters moving indicates playback disturbance; log immediately.
        if (lostPictures > _lastLostPics || corrupted > _lastCorrupted || discontinuity > _lastDiscontinuity)
        {
            RecordLogged(lostPictures, corrupted, discontinuity, currentTicks);
            return true;
        }

        // Starvation or stalled display rate during playback indicates an issue; log immediately.
        // SP-0134: compared as numbers. This compared the formatted text with "0.0" while the producer
        // formatted with the user's regional format - under a comma decimal separator it read "0,0" and
        // starved samples waited for the heartbeat. A number has no regional format.
        if (IsStarved(inKbps) || IsStarved(dispFps))
        {
            RecordLogged(lostPictures, corrupted, discontinuity, currentTicks);
            return true;
        }

        // Steady state heartbeat every 30 seconds.
        if ((currentTicks - _lastLogTicks) >= HeartbeatInterval.TotalSeconds * frequency)
        {
            RecordLogged(lostPictures, corrupted, discontinuity, currentTicks);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A rate as the STATS line writes it: one decimal, always with a dot whatever the regional format, so
    /// the log reads and greps the same on every machine; <c>n/a</c> when no rate could be measured.
    /// </summary>
    public static string FormatRate(double? rate) =>
        rate is { } value ? value.ToString("F1", CultureInfo.InvariantCulture) : "n/a";

    private static bool IsStarved(double? rate) => rate is { } value && value < StarvedRate;

    private void RecordLogged(int lostPictures, int corrupted, int discontinuity, long currentTicks)
    {
        _hasLoggedForLeg = true;
        _lastLostPics = lostPictures;
        _lastCorrupted = corrupted;
        _lastDiscontinuity = discontinuity;
        _lastLogTicks = currentTicks;
    }
}
