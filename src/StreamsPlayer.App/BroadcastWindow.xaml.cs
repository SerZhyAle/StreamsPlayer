using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0252: the window where this PC's broadcast is started, watched, shared and stopped - one card for each of the four
/// sources, which start and stop independently. A companion surface (APP-BEHAVIOUR rule 1, as the incoming list is): not
/// modal, one instance, and closing it, by Escape, the close box or Close, never stops a broadcast. Everything shown is
/// read from the producer; the window owns no broadcast state, only the stop in progress, which the producer has no
/// word for. What a start publishes is the producer's and unchanged; quality, ports, LAN listening, the permission and
/// the link password stay in Settings, and this window links there.
/// </summary>
public partial class BroadcastWindow : Window
{
    private readonly MainWindow _host;
    private readonly BroadcastProducerService _producer;
    private readonly ExchangeSourceService _exchange;
    private readonly Dictionary<BroadcastSource, BroadcastSourceCard> _cards;
    private readonly HashSet<BroadcastSource> _stopping = [];
    // The counts of listeners on the local network and the tunnels change without the producer announcing it.
    private readonly DispatcherTimer _countTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    internal BroadcastWindow(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        _producer = ((App)Application.Current).Broadcast;
        _exchange = ((App)Application.Current).Exchange;
        _cards = new()
        {
            [BroadcastSource.Screen] = ScreenCard,
            [BroadcastSource.Camera] = CameraCard,
            [BroadcastSource.Microphone] = MicrophoneCard,
            [BroadcastSource.SystemSound] = SystemSoundCard
        };

        foreach (var (source, card) in _cards)
        {
            var captured = source;
            card.Source = captured;
            card.StartRequested += (_, _) => Start(captured);
            card.CancelRequested += (_, _) => _producer.CancelStart(captured);
            card.StopRequested += (_, _) => Stop(captured);
            card.ShareRequested += (_, _) => Share(captured);
            card.CastRequested += (_, _) => Cast(captured);
            card.RecipientsRequested += (_, _) => ShowRecipients(captured);
        }

        InitializeChoices();
        _producer.Changed += OnProducerChanged;
        _exchange.Changed += OnProducerChanged;
        _countTimer.Tick += (_, _) => Refresh();
        _countTimer.Start();
        Closed += (_, _) =>
        {
            _countTimer.Stop();
            _producer.Changed -= OnProducerChanged;
            _exchange.Changed -= OnProducerChanged;
        };
        Refresh();
    }

    /// <summary>APP-SETTINGS rule 5: the lines are formatted in code, so a live language switch rebuilds them.</summary>
    internal void RefreshLocalization()
    {
        InitializeChoices();
        Refresh();
    }

    private void OnProducerChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(Refresh);
        }
    }

    private void Refresh()
    {
        var settings = _producer.Settings;
        var guest = _producer.IsGuestOnly;
        var offered = settings.Allowed && !guest;
        ShowHeader(settings, guest);

        if (offered)
        {
            LoadDevicesOnce();
        }

        var verdicts = _producer.StartVerdicts;
        var running = _producer.Running;
        var phases = BroadcastWindowRules.SourceOrder.ToDictionary(source => source, source => BroadcastWindowRules.PhaseOf(
            running.Contains(source), _producer.IsStarting(source), _stopping.Contains(source),
            !running.Contains(source) && verdicts.Any(verdict => verdict.Source == source && verdict.State == BroadcastProducerState.Failed)));
        var busy = phases.Values.Count(phase => phase is BroadcastSourcePhase.Live or BroadcastSourcePhase.Starting or BroadcastSourcePhase.Stopping);

        foreach (var source in BroadcastWindowRules.SourceOrder)
        {
            var card = _cards[source];
            var video = !BroadcastSources.IsAudioOnly(source);
            card.Visibility = offered && (!video || !_videoKnown || _videoOffered) ? Visibility.Visible : Visibility.Collapsed;
            card.Show(ViewOf(source, phases[source], busy, verdicts.FirstOrDefault(verdict => verdict.Source == source), video && !_videoOffered));
        }

        StopAllButton.IsEnabled = running.Count > 0 && _stopping.Count < running.Count;
        StopAllButton.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        VideoUnavailableText.Visibility = offered && _videoKnown && !_videoOffered ? Visibility.Visible : Visibility.Collapsed;
        var outcome = verdicts.Count == 0 && _producer.OutcomeKey is { } key ? LocalizationService.Get(key) : string.Empty;
        OutcomeText.Text = outcome;
        OutcomeText.Visibility = outcome.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The top of the window: the exchange server's status (owner decision 1), the link-password choice the next broadcast
    /// will use (decision 2, changed in Settings only), and - when nothing can be broadcast - the reason and the way to the
    /// permission (decision 3).
    /// </summary>
    private void ShowHeader(BroadcastProducerSettings settings, bool guest)
    {
        var server = LocalizationService.Get(_exchange.StatusKey);
        if (_producer.ServerNeededButUnreachable)
        {
            server += " " + LocalizationService.Get("BroadcastButtonServerWarning");
        }

        ServerStatusText.Text = server;
        LinkChoiceText.Text = LocalizationService.Format("BroadcastLinkChoiceLine", settings.Choice switch
        {
            BroadcastLinkChoice.Custom => LocalizationService.Get("BroadcastLinkCustom"),
            BroadcastLinkChoice.None => LocalizationService.Get("BroadcastLinkNone"),
            _ => LocalizationService.Get("BroadcastLinkGenerated")
        });
        LinkChoiceText.Visibility = settings.Allowed && !guest ? Visibility.Visible : Visibility.Collapsed;
        ReasonText.Text = guest ? LocalizationService.Get("BroadcastGuestOnly")
            : settings.Allowed ? string.Empty : LocalizationService.Get("BroadcastNotAllowed");
        ReasonText.Visibility = ReasonText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnableLinkButton.Visibility = !guest && !settings.Allowed ? Visibility.Visible : Visibility.Collapsed;
        // The footer's link to the same page would repeat the one under the reason.
        SettingsButton.Visibility = EnableLinkButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private BroadcastCardView ViewOf(BroadcastSource source, BroadcastSourcePhase phase, int busy, BroadcastStartVerdict? verdict, bool videoMissing)
    {
        var facts = _producer.LiveFacts(source);
        var where = new List<string>();
        if (facts is not null)
        {
            if (facts.Server is not null)
            {
                where.Add(LocalizationService.Format("BroadcastAnnounced", facts.Server, facts.Recipients.Count));
                if (facts.Recipients.Count == 0)
                {
                    where.Add(LocalizationService.Get("BroadcastNoRecipients"));
                }
            }
            else
            {
                where.Add(LocalizationService.Get("BroadcastNotAnnounced"));
            }

            if (facts.LanAddress is not null)
            {
                where.Add(LocalizationService.Format("BroadcastLanAddress", facts.LanAddress));
            }

            if (facts.Drive)
            {
                where.Add(LocalizationService.Get("BroadcastWhereDrive"));
            }

            if (facts.LanAddress is not null || facts.Tunnels > 0)
            {
                where.Add(LocalizationService.Format("BroadcastReceivers", facts.Receivers));
            }
        }

        var state = phase switch
        {
            BroadcastSourcePhase.Starting => LocalizationService.Get("BroadcastStarting"),
            BroadcastSourcePhase.Stopping => LocalizationService.Get("BroadcastStateStopping"),
            BroadcastSourcePhase.Live => LiveState(facts, verdict),
            BroadcastSourcePhase.Failed => LocalizationService.Format("BroadcastStartFailed",
                LocalizationService.Get(verdict?.ReasonKey ?? "BroadcastCaptureFailed")),
            _ => LocalizationService.Get("BroadcastStateOff")
        };

        return new BroadcastCardView(_producer.SourceTitle(source), phase, state, StartCaption(source),
            !videoMissing && BroadcastWindowRules.CanStart(phase, busy), NoteOf(source), where,
            facts is { Server: not null } && facts.Recipients.Count > 0);
    }

    private static string LiveState(BroadcastLiveFacts? facts, BroadcastStartVerdict? verdict)
    {
        var since = facts is null
            ? string.Empty
            : LocalizationService.Format("BroadcastStateLiveSince", facts.StartedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        // A live source can carry a standing problem (the relay was lost, no device can open it): it is told beside the time.
        return verdict?.ReasonKey is { } reason ? since + " " + LocalizationService.Get(reason) : since;
    }

    private static string StartCaption(BroadcastSource source) => LocalizationService.Get(source switch
    {
        BroadcastSource.Screen => "BroadcastScreenStart",
        BroadcastSource.Camera => "BroadcastCameraStart",
        BroadcastSource.Microphone => "BroadcastMicStart",
        _ => "BroadcastSoundStart"
    });

    private static string? NoteOf(BroadcastSource source) => source switch
    {
        BroadcastSource.Screen => LocalizationService.Get("BroadcastScreenPrivacy"),
        BroadcastSource.SystemSound when !WindowsAudioCapture.OwnOutputExclusionAvailable => LocalizationService.Get("BroadcastOwnOutputRisk"),
        _ => null
    };

    private void Start(BroadcastSource source) =>
        HandlerBoundary.Run(nameof(Start), () => _producer.StartAsync(source));

    private void Stop(BroadcastSource source) => HandlerBoundary.Run(nameof(Stop), async () =>
    {
        _stopping.Add(source);
        Refresh();
        try
        {
            await _producer.StopAsync(source);
        }
        finally
        {
            _stopping.Remove(source);
            Refresh();
        }
    });

    private void StopAll_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(StopAll_Click), async () =>
    {
        var live = _producer.Running;
        _stopping.UnionWith(live);
        Refresh();
        try
        {
            await _producer.StopAllAsync();
        }
        finally
        {
            _stopping.ExceptWith(live);
            Refresh();
        }
    });

    private void Share(BroadcastSource source)
    {
        try
        {
            Open(BroadcastLiveMenuBuilder.BuildShareMenu(_producer, source, this), _cards[source].ShareAnchor);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(Share), exception);
        }
    }

    private void Cast(BroadcastSource source) => HandlerBoundary.Run(nameof(Cast), async () =>
    {
        // Receivers are asked for when the menu opens (SP-0222), so the answer is waited for before the menu shows.
        Open(await BroadcastLiveMenuBuilder.BuildCastMenuAsync(_producer, _exchange, source, this), _cards[source].CastAnchor);
    });

    private static void Open(ContextMenu menu, FrameworkElement anchor)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ShowRecipients(BroadcastSource source)
    {
        var names = _producer.LiveFacts(source)?.Recipients ?? [];
        MessageBox.Show(this, _producer.SourceTitle(source) + ":" + Environment.NewLine + string.Join(Environment.NewLine, names),
            LocalizationService.Get("BroadcastShowRecipients"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _host.OpenSettings(SettingsWindow.BroadcastPageIndex);

    private void ExchangeSettings_Click(object sender, RoutedEventArgs e) => _host.OpenSettings(SettingsWindow.ExchangePageIndex);

    private void ExchangeHelp_Click(object sender, RoutedEventArgs e) => ExchangeHelp.Open(this, ExchangePortalPage.Broadcasts);

    // A companion surface is not a dialog, so there is no cancel role to lean on: Escape, the close box and Close are one
    // path, written here once. An open drop-down list takes the key first, as it does everywhere.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not ComboBox { IsDropDownOpen: true })
        {
            e.Handled = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
