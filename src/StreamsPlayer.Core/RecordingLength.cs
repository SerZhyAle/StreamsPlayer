using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0121: how long a recording ran, as the notice and the recording badge say it - <c>mm:ss</c> under an hour,
/// <c>h:mm:ss</c> from there. The badge used to print total minutes, which read 75:00 for an hour and a quarter.
/// </summary>
public static class RecordingLength
{
    public static string Format(TimeSpan length)
    {
        if (length < TimeSpan.Zero)
        {
            length = TimeSpan.Zero;
        }

        return length.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{length.Minutes:00}:{length.Seconds:00}");
    }
}
