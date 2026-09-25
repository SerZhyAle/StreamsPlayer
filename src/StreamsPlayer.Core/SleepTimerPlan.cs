namespace StreamsPlayer.Core;

/// <summary>
/// SP-0022: the pure deadline arithmetic behind the audio sleep timer. The App owns the clock,
/// the UI, and the actual stop; this type only answers "when does it end" and "is it over yet",
/// so the behaviour is testable without a window.
///
/// A timer is one absolute instant, never a countdown that drifts: Windows sleep, a paused UI
/// thread, or a long stall cannot extend it, and a deadline that passed while the machine slept
/// is simply already expired when the app looks again.
/// </summary>
public static class SleepTimerPlan
{
    /// <summary>Presets offered next to the now-playing bar, in minutes.</summary>
    public static readonly IReadOnlyList<int> PresetMinutes = [15, 30, 45, 60];

    /// <summary>
    /// Longest deadline a clock-time choice may resolve to: one wall-clock day, which is 25 hours of real
    /// time on the day an autumn clock change repeats an hour.
    /// </summary>
    public static readonly TimeSpan ClockHorizon = TimeSpan.FromHours(25);

    /// <summary>Deadline for a preset duration. Non-positive durations are rejected.</summary>
    public static DateTimeOffset? FromDuration(DateTimeOffset now, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? null : now + duration;

    /// <summary>
    /// Deadline for a wall-clock choice: the next moment the clock in <paramref name="zone"/> reads
    /// <paramref name="localTime"/>. A time that already passed today (or is exactly now) means tomorrow,
    /// so the user never gets a timer that fires instantly or in the past.
    /// </summary>
    /// <remarks>
    /// SP-0132: the offset is the zone's on the target date, not the one in force now. Taking
    /// <c>now.Offset</c> made "stop at 07:00" set the evening before a daylight-saving change fire at
    /// 06:00 or 08:00 by the clock on the wall. Two edge readings follow from "the next moment the clock
    /// reads it": a time inside a spring-forward gap is never read, so the timer stops at the first minute
    /// after the gap - the moment the clock jumps past it; a time the autumn change repeats is read twice,
    /// and the first reading still ahead wins.
    /// </remarks>
    public static DateTimeOffset FromLocalTime(DateTimeOffset now, TimeOnly localTime, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        // Two days always suffice: every reading tomorrow lies after the midnight that ends today.
        return Occurrences(today.ToDateTime(localTime), zone)
            .Concat(Occurrences(today.AddDays(1).ToDateTime(localTime), zone))
            .First(candidate => candidate > now);
    }

    /// <summary>Every instant at which the clock in <paramref name="zone"/> reads <paramref name="local"/>, earliest first.</summary>
    private static IEnumerable<DateTimeOffset> Occurrences(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsAmbiguousTime(local))
        {
            return zone.GetAmbiguousTimeOffsets(local)
                .Select(offset => new DateTimeOffset(local, offset))
                .Order();
        }

        // A skipped time is replaced by the first minute the clock does show after it. Gaps are whole
        // minutes in every zone .NET carries, so the walk ends on the gap's far edge.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return [new DateTimeOffset(local, zone.GetUtcOffset(local))];
    }

    /// <summary>Time left, clamped at zero so an overdue deadline never shows a negative countdown.</summary>
    public static TimeSpan Remaining(DateTimeOffset now, DateTimeOffset deadline)
    {
        var remaining = deadline - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>True once the deadline is reached. Idempotent: it keeps answering true afterwards.</summary>
    public static bool HasExpired(DateTimeOffset now, DateTimeOffset deadline) => now >= deadline;

    /// <summary>
    /// Countdown text for the timer button: <c>H:MM:SS</c> past an hour, otherwise <c>M:SS</c>.
    /// Culture-independent digits only - no localized words, so the App can show it as-is.
    /// </summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        // Round up so a timer never displays 0:00 while it is still running.
        var total = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        return total >= TimeSpan.FromHours(1)
            ? $"{(int)total.TotalHours}:{total.Minutes:D2}:{total.Seconds:D2}"
            : $"{(int)total.TotalMinutes}:{total.Seconds:D2}";
    }

    /// <summary>
    /// Parses a user-entered local time (<c>HH:mm</c> or <c>H:mm</c>, 24-hour). Returns null for
    /// anything the user could not have meant, so the App can reject it without guessing.
    /// </summary>
    public static TimeOnly? ParseLocalTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minute) ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59)
        {
            return null;
        }

        return new TimeOnly(hour, minute);
    }
}
