using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0075: the programme on now and the one after it, from the user's downloaded TV schedule, under
/// the channel name. Separate from the SP-0073 on-air line, which is what the stream itself announces:
/// the two answer different questions and either may be absent.
///
/// <para>The lookup is handed in by the catalog window and reads whatever schedule is current there, so a
/// new download, a removal or a rebinding reaches an open player on its next tick. Times are the
/// broadcaster's, shown in the user's local time.</para>
/// </summary>
public partial class PlayerWindow
{
    private static readonly TimeSpan ScheduleTickInterval = TimeSpan.FromSeconds(30);

    private Func<DateTimeOffset, TvScheduleNowNext>? _scheduleLookup;
    private DispatcherTimer? _scheduleTimer;

    internal void AttachTvSchedule(Func<DateTimeOffset, TvScheduleNowNext> lookup)
    {
        _scheduleLookup = lookup;
        _scheduleTimer = new DispatcherTimer { Interval = ScheduleTickInterval };
        _scheduleTimer.Tick += (_, _) => ApplyTvSchedule();
        Closed += (_, _) => _scheduleTimer.Stop();
        _scheduleTimer.Start();
        ApplyTvSchedule();
    }

    private void ApplyTvSchedule()
    {
        var reading = _scheduleLookup?.Invoke(DateTimeOffset.Now) ?? new TvScheduleNowNext(null, null);
        if (reading.Now is { } now)
        {
            ScheduleNowText.Text = LocalizationService.Format("PlayerScheduleNow", Clock(now.Start), Clock(now.Stop), now.Title);
            ScheduleNowText.Visibility = Visibility.Visible;
        }
        else
        {
            ScheduleNowText.Text = string.Empty;
            ScheduleNowText.Visibility = Visibility.Collapsed;
        }

        if (reading.Next is { } next)
        {
            ScheduleNextText.Text = LocalizationService.Format("PlayerScheduleNext", Clock(next.Start), next.Title);
            ScheduleNextText.Visibility = Visibility.Visible;
        }
        else
        {
            ScheduleNextText.Text = string.Empty;
            ScheduleNextText.Visibility = Visibility.Collapsed;
        }
    }

    private static string Clock(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("t", CultureInfo.CurrentUICulture);
}
