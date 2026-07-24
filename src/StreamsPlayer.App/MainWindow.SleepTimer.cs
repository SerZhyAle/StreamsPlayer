using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0022: audio sleep timer. One timer belongs to the inline audio session: it is an absolute
// deadline (never a countdown that drifts), it survives a station switch, and it is dropped by a
// manual stop, an explicit cancel, or app exit - it is deliberately not persisted.
public partial class MainWindow
{
    private DateTimeOffset? _sleepDeadline;
    private DispatcherTimer? _sleepTicker;

    private void SleepTimerButton_Click(object sender, RoutedEventArgs e)
    {
        if (SleepTimerButton.ContextMenu is not { } menu)
        {
            return;
        }

        BuildSleepTimerMenu(menu);
        menu.PlacementTarget = SleepTimerButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void BuildSleepTimerMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var minutes in SleepTimerPlan.PresetMinutes)
        {
            var item = new MenuItem { Header = LocalizationService.Format("SleepTimerMinutes", minutes), Tag = minutes };
            item.Click += SleepTimerPreset_Click;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        // Inline time entry: the spec forbids sending the listener to a new full-size screen for this.
        var timeBox = new TextBox { Width = 70, Margin = new Thickness(6, 0, 0, 0), MaxLength = 5 };
        timeBox.KeyDown += SleepTimerTimeBox_KeyDown;
        var timePanel = new StackPanel { Orientation = Orientation.Horizontal };
        timePanel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get("SleepTimerAtTime"),
            VerticalAlignment = VerticalAlignment.Center
        });
        timePanel.Children.Add(timeBox);
        menu.Items.Add(new MenuItem { Header = timePanel, StaysOpenOnClick = true });

        if (_sleepDeadline is not null)
        {
            menu.Items.Add(new Separator());
            var cancel = new MenuItem { Header = LocalizationService.Get("SleepTimerCancel") };
            cancel.Click += (_, _) => CancelSleepTimer(announce: true);
            menu.Items.Add(cancel);
        }
    }

    private void SleepTimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: int minutes } &&
            SleepTimerPlan.FromDuration(DateTimeOffset.Now, TimeSpan.FromMinutes(minutes)) is { } deadline)
        {
            StartSleepTimer(deadline);
        }
    }

    private void SleepTimerTimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;
        if (SleepTimerPlan.ParseLocalTime(box.Text) is not { } localTime)
        {
            SetStatus("SleepTimerInvalidTime");
            return;
        }

        if (SleepTimerButton.ContextMenu is { } menu)
        {
            menu.IsOpen = false;
        }

        StartSleepTimer(SleepTimerPlan.FromLocalTime(DateTimeOffset.Now, localTime));
    }

    private void StartSleepTimer(DateTimeOffset deadline)
    {
        _sleepDeadline = deadline;
        _sleepTicker ??= CreateSleepTicker();
        _sleepTicker.Start();
        UpdateSleepTimerButton();
        _log.Event("SLEEP TIMER SET", $"deadline={deadline:HH:mm}");
        SetStatus("SleepTimerSet", deadline.LocalDateTime.ToString("t", System.Globalization.CultureInfo.CurrentUICulture));
    }

    private DispatcherTimer CreateSleepTicker()
    {
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += SleepTicker_Tick;
        return ticker;
    }

    private void SleepTicker_Tick(object? sender, EventArgs e)
    {
        if (_sleepDeadline is not { } deadline)
        {
            _sleepTicker?.Stop();
            return;
        }

        // A deadline that passed while the machine slept is applied on the first tick after resume.
        if (SleepTimerPlan.HasExpired(DateTimeOffset.Now, deadline))
        {
            CancelSleepTimer(announce: false);
            if (_playingAudio is not null)
            {
                StopAudio();
            }

            _log.Event("SLEEP TIMER EXPIRED");
            SetStatus("SleepTimerEnded");
            return;
        }

        UpdateSleepTimerButton();
    }

    /// <summary>Drops the timer. <paramref name="announce"/> distinguishes an explicit cancel from expiry.</summary>
    private void CancelSleepTimer(bool announce)
    {
        var wasSet = _sleepDeadline is not null;
        _sleepDeadline = null;
        _sleepTicker?.Stop();
        UpdateSleepTimerButton();
        if (announce && wasSet)
        {
            _log.Event("SLEEP TIMER CANCEL");
            SetStatus("SleepTimerCancelled");
        }
    }

    /// <summary>Shows the remaining time while a timer runs; the plain label otherwise.</summary>
    private void UpdateSleepTimerButton()
    {
        if (_sleepDeadline is { } deadline)
        {
            SleepTimerButton.Content = SleepTimerPlan.FormatRemaining(SleepTimerPlan.Remaining(DateTimeOffset.Now, deadline));
            SleepTimerButton.ToolTip = LocalizationService.Format(
                "SleepTimerActiveTip",
                deadline.LocalDateTime.ToString("t", System.Globalization.CultureInfo.CurrentUICulture));
        }
        else
        {
            SleepTimerButton.Content = LocalizationService.Get("SleepTimer");
            SleepTimerButton.ToolTip = LocalizationService.Get("SleepTimerTip");
        }
    }

    /// <summary>The control follows the audio session; the deadline itself survives a station switch.</summary>
    private void ShowSleepTimerControl(bool visible)
    {
        SleepTimerButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            UpdateSleepTimerButton();
        }
    }
}
