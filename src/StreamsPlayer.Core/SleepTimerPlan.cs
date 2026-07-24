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

    /// <summary>Longest deadline a clock-time choice may resolve to.</summary>
    public static readonly TimeSpan ClockHorizon = TimeSpan.FromHours(24);

    /// <summary>Deadline for a preset duration. Non-positive durations are rejected.</summary>
    public static DateTimeOffset? FromDuration(DateTimeOffset now, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? null : now + duration;

    /// <summary>
    /// Deadline for a wall-clock choice, resolved to the next occurrence of that local time within
    /// the next 24 hours. A time that already passed today (or is exactly now) means tomorrow, so
    /// the user never gets a timer that fires instantly or in the past.
    /// </summary>
    public static DateTimeOffset FromLocalTime(DateTimeOffset now, TimeOnly localTime)
    {
        var candidate = new DateTimeOffset(
            now.Year, now.Month, now.Day,
            localTime.Hour, localTime.Minute, 0,
            now.Offset);

        return candidate > now ? candidate : candidate.AddDays(1);
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
