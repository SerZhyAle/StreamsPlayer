using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class ExternalControlWindow : Window, IHelpContext
{
    private static readonly AppCommandEntry[] RemoteCommands =
        AppCommandManifest.Embedded.Commands.Where(command => command.Remote is not null).ToArray();

    private static readonly string[] DisplayedClasses = RemoteRiskClass.All
        .Where(risk => RemoteCommands.Any(command => command.Risk == risk)).ToArray();

    private readonly MainWindow? _host;
    private readonly RemoteCommandTarget? _target;
    private RemoteCommandTarget Target => _target ?? throw new InvalidOperationException("The help capture has no live command target.");
    private readonly DispatcherTimer _consentTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private RemoteConsentQuestion? _question;
    private RemoteConnectionState _connectionState;
    private string? _lastHelpAnchor;

    string? IHelpContext.HelpSectionAnchor => _lastHelpAnchor;
    private bool _closed;

    private ExternalControlWindow() => InitializeComponent();

    internal ExternalControlWindow(MainWindow host, RemoteCommandTarget target, SettingsWindowRectangle? placement) : this()
    {
        Owner = _host = host;
        _target = target;
        if (placement is { IsUsable: true })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var wanted = new ScreenRect(placement.Left, placement.Top, placement.Width, placement.Height);
            ApplyPlacement(ScreenPlacement.Fit(wanted, MonitorWorkArea.Around(wanted, host), MinWidth, MinHeight));
        }

        _consentTimer.Tick += (_, _) => ShowCountdown();
        _target.PresentationChanged += PresentationChanged;
        _target.ConsentChanged += ConsentChanged;
        _target.LogChanged += LogChanged;
        RefreshState();
        RefreshConsent();
        RefreshCommands();
        RefreshLog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (WindowStartupLocation == WindowStartupLocation.Manual)
        {
            var wanted = new ScreenRect(Left, Top, Width, Height);
            ApplyPlacement(ScreenPlacement.Fit(wanted, MonitorWorkArea.Around(wanted, this), MinWidth, MinHeight));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _consentTimer.Stop();
        if (_target is { } target)
        {
            target.PresentationChanged -= PresentationChanged;
            target.ConsentChanged -= ConsentChanged;
            target.LogChanged -= LogChanged;
        }
        base.OnClosed(e);
    }

    private void ApplyPlacement(ScreenRect placement)
    {
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
    }

    internal SettingsWindowRectangle? CurrentPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        return bounds.Width >= MinWidth && bounds.Height >= MinHeight
            ? new SettingsWindowRectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height)
            : null;
    }

    internal void BringForward(bool showControllers)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (showControllers)
        {
            ControllersSection.IsExpanded = true;
            ControllersSection.BringIntoView();
            ControllersSection.Focus();
        }
    }

    private void PresentationChanged() => OnUi(RefreshState);
    private void ConsentChanged() => OnUi(RefreshConsent);
    private void LogChanged() => OnUi(RefreshLog);

    private void OnUi(Action refresh)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_closed)
                {
                    refresh();
                }
            });
        }
    }

    private void RefreshState()
    {
        var settings = Target.Settings;
        _connectionState = Target.ConnectionState;
        RemoteListeningOption.IsChecked = settings.Listening;
        StateText.SetResourceReference(TextBlock.TextProperty, _connectionState switch
        {
            RemoteConnectionState.Off => "RemoteStateOff",
            RemoteConnectionState.NoServer => "RemoteStateNoServer",
            RemoteConnectionState.SignInAgain => "RemoteStateSignIn",
            RemoteConnectionState.Offline => "RemoteStateOffline",
            _ => "RemoteStateReady"
        });
        ExchangeAction.Visibility = _connectionState is RemoteConnectionState.NoServer or RemoteConnectionState.SignInAgain
            ? Visibility.Visible : Visibility.Collapsed;
        ExchangeAction.SetResourceReference(ContentControl.ContentProperty,
            _connectionState == RemoteConnectionState.SignInAgain ? "RemoteSignInAgain" : "RemoteOpenExchange");
        RefreshControllers(settings);
    }

    private void Listening_Click(object sender, RoutedEventArgs e) =>
        HandlerBoundary.Run(nameof(Listening_Click), SetListeningAsync);

    private async Task SetListeningAsync()
    {
        var listening = RemoteListeningOption.IsChecked;
        RemoteListeningOption.IsEnabled = false;
        var saved = await Target.UpdateSettingsAsync(settings => settings with { Listening = listening }, "external-control");
        if (!_closed)
        {
            RemoteListeningOption.IsEnabled = true;
            ListeningError.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
            RefreshState();
        }
    }

    private void ExchangeAction_Click(object sender, RoutedEventArgs e) =>
        _host?.OpenSettings(SettingsWindow.ExchangePageIndex, focusExchangeSignIn: _connectionState == RemoteConnectionState.SignInAgain);

    private void RefreshConsent()
    {
        _question = Target.PendingConsent;
        ConsentSection.Visibility = _question is null ? Visibility.Collapsed : Visibility.Visible;
        if (_question is not { } question)
        {
            _consentTimer.Stop();
            return;
        }

        PopulateConsent(question);
        ShowCountdown();
        _consentTimer.Start();
    }

    private void PopulateConsent(RemoteConsentQuestion question)
    {
        ConsentSection.IsExpanded = true;
        ConsentTitle.Text = RemoteControlText.Title(question);
        ConsentCommand.Text = RemoteControlText.Command(question);
        ConsentKind.Text = RemoteControlText.Kind(question);
    }

    private void ShowCountdown()
    {
        if (_question is { } question)
        {
            ConsentCountdown.Text = RemoteControlText.Countdown(question);
        }
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => _question?.Finish(true);
    private void Decline_Click(object sender, RoutedEventArgs e) => _question?.Finish(false);

    private void RefreshCommands()
    {
        CommandsList.ItemsSource = RemoteCommands.Select(command =>
        {
            var words = $"{RemoteControlText.Summary(command)} - {RemoteControlText.ClassName(command.Risk)}";
            return command.Remote!.ConfirmsOnDevice ? $"{words} - {LocalizationService.Get("RemoteAsksHere")}" : words;
        }).ToArray();
    }

    private void RefreshLog()
    {
        const int recentCount = 20;
        var entries = Target.LatestLog(recentCount);
        RecentRequests.ItemsSource = entries.Select(FormatLogLine).ToArray();
        LogEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (FullLog.Visibility == Visibility.Visible)
        {
            UpdateFullLog();
        }
    }

    private static string FormatLogLine(RemoteCommandLogEntry entry)
    {
        var device = entry.Verified
            ? (string.IsNullOrWhiteSpace(entry.ControllerName) ? entry.ControllerId : entry.ControllerName)
            : LocalizationService.Format("RemoteUnverifiedDevice", entry.ControllerId);
        var verb = RemoteCommands.FirstOrDefault(command => command.Name == entry.Verb);
        var summary = verb is not null ? RemoteControlText.Summary(verb) : entry.Verb.Length == 0 ? "-" : entry.Verb;
        return $"{RemoteControlText.Time(entry.At)} {device} - {summary}: {RemoteControlText.Outcome(entry.Outcome)}";
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        FullLog.Visibility = Visibility.Visible;
        UpdateFullLog();
        FullLog.Focus();
        FullLog.BringIntoView();
    }

    private void UpdateFullLog()
    {
        var entries = Target.LatestLog(RemoteCommandLog.MaximumEntries);
        FullLog.Text = entries.Count == 0 ? LocalizationService.Get("RemoteLogEmpty") :
            string.Join(Environment.NewLine, entries.Select(FormatFullLogLine));
    }

    private static string FormatFullLogLine(RemoteCommandLogEntry entry)
    {
        var arguments = string.Join(" ", entry.Arguments ?? []);
        return $"{FormatLogLine(entry)} [{entry.ControllerId}; {entry.RiskClass}; {entry.Exit}] {arguments}".TrimEnd();
    }

    private void Window_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject current && HelpService.FindAnchor(current, this) is { } anchor)
        {
            _lastHelpAnchor = anchor;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
