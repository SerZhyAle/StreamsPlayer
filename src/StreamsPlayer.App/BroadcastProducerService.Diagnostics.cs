using System.Globalization;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal enum BroadcastStopCause { Person, SignOut, ApplicationExit, CaptureLoss, StartRollback, SettingsChanged }
internal enum BroadcastProducerState { Starting, Live, Failed, Stopped }

internal sealed record BroadcastStartVerdict(BroadcastSource Source, BroadcastProducerState State,
    string? ReasonKey = null, string? Server = null, IReadOnlyList<string>? Recipients = null, string? LanAddress = null)
{
    internal string Text
    {
        get
        {
            var parts = new List<string>();
            if (State == BroadcastProducerState.Starting) parts.Add(LocalizationService.Get("BroadcastStarting"));
            else if (State == BroadcastProducerState.Failed)
                parts.Add(LocalizationService.Format("BroadcastStartFailed", LocalizationService.Get(ReasonKey ?? "BroadcastCaptureFailed")));
            else if (State == BroadcastProducerState.Stopped) parts.Add(LocalizationService.Get("BroadcastStopped"));
            else
            {
                if (Server is not null)
                {
                    parts.Add(LocalizationService.Format("BroadcastAnnounced", Server, Recipients?.Count ?? 0));
                    if (Recipients?.Count == 0) parts.Add(LocalizationService.Get("BroadcastNoRecipients"));
                }
                else parts.Add(LocalizationService.Get("BroadcastNotAnnounced"));
                if (LanAddress is not null) parts.Add(LocalizationService.Format("BroadcastLanAddress", LanAddress));
                if (ReasonKey is not null) parts.Add(LocalizationService.Get(ReasonKey));
            }
            return BroadcastProducerService.TitleOf(Source) + ": " + string.Join(" ", parts);
        }
    }


}

/// <summary>What the broadcast window tells about a live source (SP-0252 requirement 3).</summary>
/// <param name="Receivers">The listeners this PC itself can count: the local network's and the tunnels'. The relay's are the server's to count.</param>
internal sealed record BroadcastLiveFacts(DateTimeOffset StartedAt, string? Server, IReadOnlyList<string> Recipients,
    string? LanAddress, int LanListeners, int Tunnels, bool Drive)
{
    internal int Receivers => LanListeners + Tunnels;
}

internal sealed partial class BroadcastProducerService
{
    private readonly Dictionary<BroadcastSource, BroadcastStartVerdict> _verdicts = [];
    private readonly HashSet<BroadcastSource> _starting = [];
    private readonly Dictionary<BroadcastSource, CancellationTokenSource> _startCancels = [];
    private readonly Action<string> _diagnostic;

    internal IReadOnlyList<BroadcastStartVerdict> StartVerdicts
    {
        get { lock (_gate) return _verdicts.Values.OrderBy(item => item.Source).ToArray(); }
    }

    internal bool IsStarting(BroadcastSource source) { lock (_gate) return _starting.Contains(source); }

    /// <summary>
    /// SP-0252 requirement 3: where a live source went. Null while the source is not running. The server is null until the
    /// server took the announcement and again after the session is lost, exactly as the start verdict reports it.
    /// </summary>
    internal BroadcastLiveFacts? LiveFacts(BroadcastSource source)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(source, out var state))
            {
                return null;
            }

            return new BroadcastLiveFacts(state.StartedAt, state.ExchangeStarted ? state.AnnouncedServer : null,
                state.RecipientNames,
                state.LanAddress is null ? null : state.LanAddress + ":" + state.LocalPort.ToString(CultureInfo.InvariantCulture),
                state.LastLanListeners, state.TunnelCount, _drive is { SignedIn: true });
        }
    }

    /// <summary>Owner decision 1 of SP-0252: the header button's warning mark.</summary>
    internal bool ServerNeededButUnreachable
    {
        get
        {
            lock (_gate)
            {
                return BroadcastWindowRules.ShowsServerWarning(_running.Count, _exchange.IsActive, _exchange.MayBroadcast,
                    _exchange.PublicEndpoint is not null);
            }
        }
    }

    // Only fixed operation names/codes, counts and endpoint facts cross this boundary. Never pass a wire envelope,
    // descriptor, device id, key, credential, exception message or server-supplied refusal text here.
    private void Diagnose(BroadcastSource source, string step, string facts = "") =>
        _diagnostic("BROADCAST | source=" + BroadcastSources.WireName(source) + " | step=" + step
            + (facts.Length == 0 ? "" : " | " + facts));

    private void SetVerdict(BroadcastStartVerdict verdict)
    {
        lock (_gate) _verdicts[verdict.Source] = verdict;
        Changed?.Invoke();
    }

    private bool FailStart(BroadcastSource source, string key)
    {
        Diagnose(source, "start-refused", "code=" + key);
        SetVerdict(new(source, BroadcastProducerState.Failed, key));
        Report(key);
        return false;
    }

    private void ReportState(BroadcastRunningSource state, string key)
    {
        state.FailureKey = key;
        UpdateRouteVerdict(state);
        Report(key);
    }

    private void UpdateRouteVerdict(BroadcastRunningSource state, bool started = false)
    {
        lock (_gate)
        {
            if (!IsRunning(state) || !started && (!_verdicts.TryGetValue(state.Source, out var old)
                || old.State != BroadcastProducerState.Live)) return;
            _verdicts[state.Source] = new(state.Source, BroadcastProducerState.Live, state.FailureKey,
                state.ExchangeStarted ? state.AnnouncedServer : null,
                state.RecipientNames, state.LanAddress is null ? null : state.LanAddress + ":" + state.LocalPort.ToString(CultureInfo.InvariantCulture));
        }
        Changed?.Invoke();
    }

    private string ReachabilityFacts(string? address, bool reachable) =>
        // S4-3 / S4-4 (DIAGNOSTIC-REPORT section 10, ListenerBindLog): the log may be mailed, so it records that a LAN address
        // is bound and that the session is online, never this machine's address or the exchange server's host and port.
        $"reachable={reachable} | lan-enabled={Settings.LanListening} | lan-bound={address is not null}"
        + $" | session-online={_exchange.PublicEndpoint is not null}"
        + $" | guest={IsGuestOnly} | sealed={_exchange.ServerSeals}"
        + $" | relay={_exchange.Features.Contains("relay")} | tunnel={_exchange.Features.Contains("tunnel")}"
        + $" | own-key={_exchange.Account.E2ePrivateKey is not null}";

    private static string SafeEndpoint(string? endpoint) =>
        LaunchableAddress.TryParse("https://" + endpoint, out var uri)
        && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && Uri.CheckHostName(uri.Host) != UriHostNameType.Unknown && uri.Port is > 0 and <= 65535
            ? uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) : "none";

    private string[] RecipientNames(ExchangeSealedBox box) => box.Keys
        .Where(key => key.To != _exchange.Account.DeviceId)
        .Select(key => _exchange.Directory?.Devices?.FirstOrDefault(device => device.DeviceId == key.To)?.DeviceName)
        .Select(name => string.IsNullOrWhiteSpace(name) ? LocalizationService.Get("BroadcastUnnamedDevice") : name)
        .ToArray();

    private static string StopCauseName(BroadcastStopCause cause) => cause switch
    {
        BroadcastStopCause.Person => "person", BroadcastStopCause.SignOut => "sign-out",
        BroadcastStopCause.ApplicationExit => "application-exit", BroadcastStopCause.CaptureLoss => "capture-loss",
        BroadcastStopCause.StartRollback => "start-rollback", _ => "settings-changed"
    };
}
