using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>A section's heading: whose broadcasts follow, and for a guest entry the line that says it is only what was given.</summary>
internal sealed record BroadcastSectionHeading(string Title, string? Note)
{
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;

    // The item peer takes its name from ToString(); a guest's section is announced with its note, because the
    // note is what tells a screen-reader user that this list is narrower than the account's.
    public override string ToString() => string.IsNullOrEmpty(Note) ? Title : $"{Title}. {Note}";
}

/// <summary>One line of a section that is not a broadcast: why there is no list, or that it is empty.</summary>
internal sealed record BroadcastSectionNote(string Text)
{
    public override string ToString() => Text;
}

/// <summary>The group heading: the device's name and presence (SP-0201 requirement 2).</summary>
internal sealed record BroadcastDeviceHeading(string DeviceName, string PresenceText)
{
    // The list box's item peer takes the accessible name from ToString(), and a record's own prints every
    // member. The heading is read as the device and whether it is on the air - exactly what is on screen.
    public override string ToString() => $"{DeviceName} {PresenceText}";
}

/// <summary>A broadcast opened by link, as long as its window is open, with the state of that window (SP-0222 requirement 4).</summary>
internal sealed record BroadcastLinkRow(string Title, string StateText)
{
    public override string ToString() => $"{Title}, {StateText}";
}

/// <summary>
/// One broadcast of the view. The address is deliberately absent: a relay or tunnel address is the
/// capability to listen, so it is never shown here, never a tooltip, never a title (SP-0201 req 5).
/// </summary>
/// <param name="OpenState">The state of this broadcast's window when one is open, already in words.</param>
/// <param name="IsGuest">The broadcast was given to this PC as a guest; it is played but never kept in the library.</param>
internal sealed record BroadcastRow(ExchangeBroadcastView View, string? OpenState = null, bool IsGuest = false,
    ExchangeAccount? ListingEntry = null, bool IsDrive = false)
{
    public string Title => string.IsNullOrWhiteSpace(View.Record.Title) ? ModeLabel : View.Record.Title!;

    public string Detail => (View.Support switch
    {
        ExchangeBroadcastSupport.Unsupported => LocalizationService.Get("BroadcastsUnsupportedReason"),
        ExchangeBroadcastSupport.UnsupportedSchema => LocalizationService.Get(ExchangeFailureRows.FormatUnsupported.MessageKey),
        // SP-0226 / item DP: not protected end to end, so not played through the server - said, never hidden.
        ExchangeBroadcastSupport.Unsealed => LocalizationService.Get("BroadcastsUnsealedReason"),
        _ when SealedState != ExchangeSealedState.None => LocalizationService.Get(SealedState.MessageKey()),
        _ when IsDrive && OpenState == LocalizationService.Get("BroadcastsStateFailed")
            && View.Record.Descriptor is { } descriptor
            && !descriptor.Endpoints.Any(endpoint => ExchangeEndpointKinds.IsExchangeAddress(endpoint.Url)) => LocalizationService.Get("DriveUnreachable"),
        _ => OpenState is null ? ModeLabel : $"{ModeLabel}, {OpenState}"
    }) + (IsDrive ? " - " + LocalizationService.Get("DriveTitle") : "");

    /// <summary>SP-0226: why this sealed broadcast's box could not be opened on this device (item DR), or none.</summary>
    private ExchangeSealedState SealedState => ExchangeBroadcastKeys.FailureOf(View.Record.BroadcastId);

    public Visibility ActionsVisibility =>
        View.Support == ExchangeBroadcastSupport.Supported && SealedState == ExchangeSealedState.None ? Visibility.Visible : Visibility.Collapsed;

    // A guest's grant ends when the owner ends it; a library row made from it would outlive the right to listen.
    public Visibility KeepVisibility => IsGuest ? Visibility.Collapsed : Visibility.Visible;

    public string PlayName => LocalizationService.Format("BroadcastsPlayNamed", Title);

    public string KeepName => LocalizationService.Format("BroadcastsKeepNamed", Title);

    // What a screen reader announces for the row (the item peer's name is ToString()). A record's own
    // prints View, and View carries the descriptor's address - the capability to listen, which this
    // window exists never to expose (SP-0201 req 5). The row is read as its title and its detail line.
    public override string ToString() => $"{Title}, {Detail}";

    private string ModeLabel => View.Record.Mode switch
    {
        FastMediaSorterBroadcastDescriptor.VideoAudioMode => LocalizationService.Get("BroadcastsVideo"),
        FastMediaSorterBroadcastDescriptor.VideoOnlyMode => LocalizationService.Get("BroadcastsVideoOnly"),
        _ => LocalizationService.Get("BroadcastsAudio")
    };
}

/// <summary>
/// SP-0222: the account's live broadcasts, grouped by the device that makes them, shown the moment the list is
/// opened and followed while it stays open - there is no refresh button, and closing it ends the following. A
/// companion surface (APP-BEHAVIOUR rule 1, widened 0.10): not modal, one instance, Escape and the close box and
/// Close the same safe path. Sections: the own account's, one per guest entry (only what was given to the guest,
/// said in its heading), and what was opened by link. Play opens a broadcast in a window of its own, beside the
/// others and beside the station in the main window; Keep is the earlier SP-0201 act. The window owns no
/// library state of its own.
/// </summary>
public partial class BroadcastsWindow : Window
{
    private void ExchangeHelp_Click(object sender, RoutedEventArgs e)
        => ExchangeHelp.Open(this, ExchangePortalPage.Broadcasts);

    private readonly MainWindow _host;
    private readonly ExchangeSourceService _source;
    private readonly IDisposable _directoryLease;
    private readonly IReadOnlyList<ExchangeGuestDirectoryViewer> _viewers;
    private readonly ObservableCollection<object> _rows = [];
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _stateSignature = string.Empty;

    internal BroadcastsWindow(MainWindow host, ExchangeSourceService source)
    {
        InitializeComponent();
        _host = host;
        _source = source;
        InitializeDriveView();
        BroadcastList.ItemsSource = _rows;
        _source.DirectoryChanged += SourceChanged;
        _host.IncomingBroadcastsChanged += Rebuild;
        // Opening the list is what asks the server for the account's broadcasts, and the guest entries each get
        // a read-only session of their own; closing it gives all of them back.
        _directoryLease = _source.AcquireDirectory(follow: true);
        _viewers = _source.OpenGuestViewers(exception =>
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(ExchangeGuestDirectoryViewer)}", exception, notifyUser: false));
        foreach (var viewer in _viewers)
        {
            viewer.Changed += SourceChanged;
        }

        _stateTimer.Tick += (_, _) => RebuildWhenWindowStatesChanged();
        _stateTimer.Start();
        Closed += (_, _) =>
        {
            _stateTimer.Stop();
            _source.DirectoryChanged -= SourceChanged;
            _host.IncomingBroadcastsChanged -= Rebuild;
            foreach (var viewer in _viewers)
            {
                viewer.Changed -= SourceChanged;
                viewer.Dispose();
            }

            _directoryLease.Dispose();
        };
        Rebuild();
    }

    private void SourceChanged()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(() => HandlerBoundary.Run(nameof(SourceChanged), async () =>
            {
                foreach (var viewer in _viewers)
                {
                    if (viewer.Snapshot is { } snapshot)
                    {
                        await _host.ApplyGuestDirectoryAsync(viewer.Entry, snapshot);
                    }
                }

                Rebuild();
            }));
        }
    }

    // A video broadcast's window has no event to hear: its state is read off the window once a second, and the
    // list is rebuilt only when what it would show has changed.
    private void RebuildWhenWindowStatesChanged()
    {
        if (StateSignature(_host.IncomingBroadcasts) != _stateSignature)
        {
            Rebuild();
        }
    }

    private static string StateSignature(IReadOnlyList<IncomingBroadcastEntry> incoming) =>
        string.Join('|', incoming.Select(entry => $"{entry.ChannelId}:{entry.State}:{entry.MessageKey}"));

    private void Rebuild()
    {
        var incoming = _host.IncomingBroadcasts;
        _stateSignature = StateSignature(incoming);
        _rows.Clear();
        var account = _source.Account;
        var accountSections = 0;
        var needsSignIn = false;
        // SP-0225: the connection check looks at the first entry that cannot list, with that entry's own status line.
        _diagnosis = null;

        if (account.Token is not null || Drive.SignedIn)
        {
            accountSections++;
            var failing = AddSection(account.Guest, account.Token is null ? "" : account.Login, account.Host,
                DriveDirectory.Merge(_source.Directory, Drive.Directory),
                account.Token is null ? Drive.StatusKey : _source.StatusKey, false, incoming);
            needsSignIn |= failing;
            if (failing && account.Token is not null)
            {
                _diagnosis ??= ExchangeDiagnosisRequest.ForStatus(_source.EnrollmentOutcomeKey, _source.StatusKey, account.Host, account.Port);
            }
        }

        foreach (var viewer in _viewers)
        {
            accountSections++;
            var failing = AddSection(true, viewer.Entry.Login, viewer.Entry.Host, viewer.Snapshot, viewer.StatusKey, viewer.Ended, incoming, viewer.Entry);
            needsSignIn |= failing;
            if (failing)
            {
                _diagnosis ??= ExchangeDiagnosisRequest.ForStatus(null, viewer.StatusKey, viewer.Entry.Host, viewer.Entry.Port);
            }
        }

        var links = incoming.Where(entry => entry.OpenedByLink).ToList();
        if (links.Count > 0)
        {
            _rows.Add(new BroadcastSectionHeading(LocalizationService.Get("BroadcastsLinkHeading"), null));
            foreach (var entry in links)
            {
                _rows.Add(new BroadcastLinkRow(entry.Title, StateText(entry)));
            }
        }

        // Nobody is signed in: the exchange's own status line says why there is no list, and the next step is one click.
        StatusText.Text = accountSections == 0
            ? LocalizationService.Get(Drive.Available && Drive.StatusKey != "DriveSignedOut" ? Drive.StatusKey : _source.StatusKey)
            : string.Empty;
        StatusText.Visibility = accountSections == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = accountSections == 0 || needsSignIn ? Visibility.Visible : Visibility.Collapsed;
        DiagnoseButton.Visibility = _diagnosis is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Adds one entry's section; returns true when the entry cannot list until the person signs in again.</summary>
    private bool AddSection(
        bool guest,
        string login,
        string host,
        ExchangeDirectorySnapshot? snapshot,
        string statusKey,
        bool ended,
        IReadOnlyList<IncomingBroadcastEntry> incoming,
        ExchangeAccount? listingEntry = null)
    {
        _rows.Add(guest
            ? new BroadcastSectionHeading(LocalizationService.Format("BroadcastsGuestHeading", login, host),
                LocalizationService.Get("BroadcastsGuestNote"))
            : new BroadcastSectionHeading(string.IsNullOrEmpty(login) ? LocalizationService.Get("DriveTitle")
                : Drive.SignedIn ? LocalizationService.Get("BroadcastsTitle")
                : LocalizationService.Format("BroadcastsAccountHeading", login, host), null));
        if (!guest && Drive.SignedIn)
        {
            _rows.Add(new BroadcastSectionNote(LocalizationService.Get("DriveNoCast")));
            if (Drive.StatusKey is "DriveReadFailed" or "DrivePublishFailed")
                _rows.Add(new BroadcastSectionNote(LocalizationService.Get(Drive.StatusKey)));
        }
        if (snapshot is null)
        {
            // A missing snapshot is the connection being off, between connections or refused - not an empty
            // account: the line says which, and the empty text stays for a listed-and-empty directory.
            _rows.Add(new BroadcastSectionNote(LocalizationService.Get(statusKey)));
            // Connecting and a list still on its way pass by themselves; every other line (signed out, refused,
            // unreachable, a certificate to approve) is one the person acts on in Settings.
            return ended || statusKey is not ("ExchangeConnecting" or "ExchangeOnline");
        }

        if (snapshot.Groups.Count == 0)
        {
            _rows.Add(new BroadcastSectionNote(LocalizationService.Get(guest ? "BroadcastsGuestEmpty" : "BroadcastsEmpty")));
            return false;
        }

        foreach (var group in snapshot.Groups)
        {
            var presence = LocalizationService.Get(group.IsOnline ? "BroadcastsOnline" : "BroadcastsOffline");
            // SP-0226 / items DG and DR: a device this program will not seal to says why, beside its name.
            if (!guest && !IsDriveDevice(group.DeviceId) && snapshot.Devices?.FirstOrDefault(device => device.DeviceId == group.DeviceId) is { } listed
                && _source.StateOfDevice(listed) is var deviceState and not ExchangeSealedState.None)
            {
                presence += " - " + LocalizationService.Get(deviceState.MessageKey());
            }

            _rows.Add(new BroadcastDeviceHeading(group.DeviceName, presence));
            foreach (var view in group.Broadcasts)
            {
                var open = view.Record.Descriptor?.SourceId is { } sourceId
                    ? incoming.FirstOrDefault(entry => entry.SourceId == sourceId)
                    : null;
                _rows.Add(new BroadcastRow(view, open is null ? null : StateText(open), guest, listingEntry, IsDriveView(view)));
            }
        }

        return false;
    }

    private static string StateText(IncomingBroadcastEntry entry) => LocalizationService.Get(entry.State switch
    {
        BroadcastLegState.Connecting => "BroadcastsStateConnecting",
        BroadcastLegState.Playing => "BroadcastsStatePlaying",
        BroadcastLegState.Reconnecting => "BroadcastsStateReconnecting",
        BroadcastLegState.Ended => "BroadcastsStateEnded",
        BroadcastLegState.Failed => "BroadcastsStateFailed",
        _ => "BroadcastsStateStopped"
    });

    private void ShowResult(BroadcastOpenResult result)
    {
        if (result.MessageKey is not { } key)
        {
            MessageText.Visibility = Visibility.Collapsed;
            return;
        }

        MessageText.Text = key == IncomingBroadcastFailure.BoundReachedKey
            ? LocalizationService.Format(key, IncomingBroadcastWindows.Default.Maximum)
            : LocalizationService.Get(key);
        MessageText.Visibility = Visibility.Visible;
    }

    private async Task PlayAsync(BroadcastRow row)
    {
        var result = row.IsDrive ? await _host.OpenDriveBroadcastWindowAsync(row.View) : row.IsGuest
            ? await _host.OpenGuestBroadcastWindowAsync(row.View, row.ListingEntry!)
            : await _host.OpenExchangeBroadcastWindowAsync(row.View);
        ShowResult(result);
    }

    // A companion surface is not a dialog, so there is no cancel role to lean on: Escape, the close box and
    // Close are one path, written here once.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void BroadcastList_KeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.Enter || BroadcastList.SelectedItem is not BroadcastRow { View.Support: ExchangeBroadcastSupport.Supported } row)
            {
                return;
            }

            e.Handled = true;
            await PlayAsync(row);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(BroadcastList_KeyDown)}", exception);
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is BroadcastRow row)
            {
                await PlayAsync(row);
            }
        }
        catch (Exception exception)
        {
            // Window-qualified: the once-per-handler notice set is keyed by name, and another window has a Play_Click.
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(Play_Click)}", exception);
        }
    }

    private async void Keep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.Tag is BroadcastRow row)
            {
                if (row.IsDrive) await _host.KeepDriveBroadcastAsync(row.View);
                else await _host.KeepExchangeBroadcastAsync(row.View);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(Keep_Click)}", exception);
        }
    }

    private void LinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenLink_Click(sender, e);
        }
    }

    private async void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = LinkBox.Text.Trim();
            if (text.Length == 0)
            {
                return;
            }

            var result = await _host.OpenBroadcastByLinkAsync(text);
            ShowResult(result);
            if (result.Outcome is BroadcastOpenOutcome.Opened or BroadcastOpenOutcome.AlreadyOpen)
            {
                LinkBox.Clear();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(OpenLink_Click)}", exception);
        }
    }

    private ExchangeDiagnosisRequest? _diagnosis;

    private void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_diagnosis is { } request)
            {
                new ExchangeDiagnosisWindow(request) { Owner = this }.ShowDialog();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(Diagnose_Click)}", exception);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _host.OpenSettings(SettingsWindow.ExchangePageIndex);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report($"{nameof(BroadcastsWindow)}.{nameof(OpenSettings_Click)}", exception);
        }
    }
}
