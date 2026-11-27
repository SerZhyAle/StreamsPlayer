using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// STRWIN-0303: the target side of <c>REMOTE-CONTROL</c> 0.4. Requests are handled one at a time, in the order they
/// arrived (rule 12): the ring answers a repeat, the time window refuses a late one, the policy of rules 8 to 10 decides,
/// the person is asked when the grant says so, the verb runs through the same dispatcher a local caller reaches, the
/// ring is written before the answer leaves, and every request that opened gets one log line (rule 11).
/// </summary>
internal sealed partial class RemoteCommandTarget
{
    /// <summary>Rule 10: the person has 60 seconds to accept; silence is <c>declined</c>.</summary>
    internal static readonly TimeSpan ConsentWindow = TimeSpan.FromSeconds(60);

    private readonly RemoteControlStore _store;
    private readonly ExchangeSourceService _exchange;
    private readonly Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>> _execute;
    private readonly CurrentLog? _log;
    private readonly Channel<RemoteCommandInbound> _queue = Channel.CreateUnbounded<RemoteCommandInbound>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private RemoteCommandRing _ring = new();
    private readonly object _logGate = new();
    private RemoteCommandLog _commandLog = new();

    internal RemoteCommandTarget(string dataDirectory, ExchangeSourceService exchange,
        Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>> execute, CurrentLog? log)
    {
        _store = new RemoteControlStore(dataDirectory);
        _exchange = exchange;
        _execute = execute;
        _log = log;
    }

    /// <summary>The person's switch and grants as last loaded or saved. Off until the document says otherwise.</summary>
    private volatile RemoteControlSettings _settings = RemoteControlSettings.Off;
    internal RemoteControlSettings Settings => _settings;

    /// <summary>
    /// Displays the shared question on this device. A missing presentation hook declines it (rule 10).
    /// </summary>
    internal Action<RemoteConsentQuestion>? ShowConsent { get; set; }

    /// <summary>Raised after a log line is added, on the worker; a view marshals it to its own thread.</summary>
    internal event Action? LogChanged;

    internal IReadOnlyList<RemoteCommandLogEntry> LatestLog(int count)
    {
        lock (_logGate)
        {
            return _commandLog.Latest(count);
        }
    }

    /// <summary>Loads the three documents, then starts taking requests. Requests that arrive before that wait in the queue.</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        _exchange.CommandRequested += inbound => _queue.Writer.TryWrite(inbound);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = await _store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            _ring = await _store.LoadRingAsync(cancellationToken).ConfigureAwait(false);
            var log = await _store.LoadLogAsync(cancellationToken).ConfigureAwait(false);
            lock (_logGate)
            {
                _commandLog = log;
            }
        }
        finally
        {
            _settingsGate.Release();
        }

        _exchange.DirectoryChanged += OnDirectoryChanged;
        _exchange.Changed += OnExchangeChanged;
        cancellationToken.Register(StopFollowing);
        OnExchangeChanged();
        LogChanged?.Invoke();
        _ = Task.Run(() => DrainAsync(cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// The person's own act in this program (rule 8) - the only path that turns the switch on or adds a grant - and the
    /// drop of rule 9. <paramref name="change"/> is applied to the settings held at the moment of the save, under one
    /// gate, so two changes never overwrite each other. False when the document could not be written: nothing changed.
    /// </summary>
    internal async Task<bool> UpdateSettingsAsync(Func<RemoteControlSettings, RemoteControlSettings> change, string cause)
    {
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = change(Settings);
            if (ReferenceEquals(settings, Settings))
            {
                return true;
            }

            if (!await _store.SaveSettingsAsync(settings).ConfigureAwait(false))
            {
                return false;
            }

            _settings = settings;
            OnExchangeChanged();
            _log?.Event("REMOTE LISTENING", $"on={settings.Listening}", $"controllers={settings.Grants.Count}", $"cause={cause}");
            return true;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var inbound in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(inbound, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One request's failure never stops the queue; the controller's wait ends it (timeout).
                    _log?.Error("Remote command failed", exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The application is closing.
        }
    }

    private async Task HandleAsync(RemoteCommandInbound inbound, CancellationToken cancellationToken)
    {
        if (!inbound.Opened)
        {
            // Rule 4: no answer, and the routing id is logged as unverified.
            await AddLogAsync(new RemoteCommandLogEntry(DateTimeOffset.UtcNow, string.Empty, inbound.ControllerId, false,
                string.Empty, string.Empty, RemoteCommandLog.SealedFailed, 0, []), cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_ring.Prune(now))
        {
            await _store.SaveRingAsync(_ring, cancellationToken).ConfigureAwait(false);
        }

        var controllerName = Settings.GrantFor(inbound.ControllerId)?.DeviceName ?? string.Empty;
        var request = RemoteCommandRequest.TryParse(inbound.Payload, inbound.RequestId, out var malformed);
        if (request is null)
        {
            await FinishAsync(inbound, controllerName, null, string.Empty, malformed!, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var optionNames = request.Args.ValueKind == JsonValueKind.Object
            ? request.Args.EnumerateObject().Select(member => member.Name).ToArray()
            : [];
        if (_ring.AnswerOf(inbound.ControllerId, inbound.RequestId) is { } stored)
        {
            // Rule 12: answered again with the stored answer, never run again.
            await inbound.ReplyAsync(Encoding.UTF8.GetBytes(stored)).ConfigureAwait(false);
            await AddLogAsync(new RemoteCommandLogEntry(now, controllerName, inbound.ControllerId, true, request.Verb,
                string.Empty, RemoteCommandLog.Repeated, 0, RemoteCommandLog.RedactedArguments(optionNames)), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (RemoteCommandWindow.IsExpired(request.IssuedAt, request.TtlSeconds, now))
        {
            await FinishAsync(inbound, controllerName, request, string.Empty, RemoteCommandAnswer.Expired(inbound.RequestId),
                optionNames, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_ring.IsFull)
        {
            // Not recorded: dropping an entry early could let a repeat run twice; refusing loses nothing.
            await inbound.ReplyAsync(RemoteCommandAnswer.Serialize(AppCommandResponse.Busy(inbound.RequestId,
                RemoteCommandMessages.RingFull))).ConfigureAwait(false);
            await AddLogAsync(new RemoteCommandLogEntry(now, controllerName, inbound.ControllerId, true, request.Verb,
                string.Empty, AppCommandStatus.Busy, AppCommandExit.Resources, RemoteCommandLog.RedactedArguments(optionNames)),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Settings.Listening && !IsController(inbound.ControllerId))
        {
            await FinishAsync(inbound, controllerName, request, string.Empty, RemoteCommandAnswer.NotPermitted(inbound.RequestId),
                optionNames, cancellationToken).ConfigureAwait(false);
            return;
        }

        var decision = RemoteControlPolicy.Decide(inbound.RequestId, Settings, inbound.ControllerId, request.Verb,
            AppCommandManifest.Embedded);
        var riskClass = decision.Entry?.Risk ?? string.Empty;
        if (decision.Kind == RemoteDecisionKind.Refuse)
        {
            await FinishAsync(inbound, controllerName, request, riskClass, decision.Refusal!, optionNames, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var entry = decision.Entry!;
        var words = RemoteCommandArguments.ToWords(entry, request.Args, inbound.RequestId, out var badArgument);
        if (words is null)
        {
            await FinishAsync(inbound, controllerName, request, riskClass, badArgument!, optionNames, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (decision.Kind == RemoteDecisionKind.Ask)
        {
            var question = new RemoteConsentQuestion(controllerName, entry, words, DateTimeOffset.UtcNow + ConsentWindow);
            if (!await AskAsync(question, cancellationToken).ConfigureAwait(false))
            {
                await FinishAsync(inbound, controllerName, request, riskClass, RemoteCommandAnswer.Declined(inbound.RequestId),
                    optionNames, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!IsController(inbound.ControllerId) ||
                RemoteControlPolicy.Decide(inbound.RequestId, Settings, inbound.ControllerId, request.Verb,
                    AppCommandManifest.Embedded).Kind == RemoteDecisionKind.Refuse)
            {
                await FinishAsync(inbound, controllerName, request, riskClass, RemoteCommandAnswer.NotPermitted(inbound.RequestId),
                    optionNames, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Rule 12 across a restart: the ring holds the request before it runs, so a verb that ends this process
        // (quit) is never run a second time by a repeated box.
        var keepUntil = RemoteCommandWindow.KeepUntil(request.IssuedAt, request.TtlSeconds);
        _ring.Record(inbound.ControllerId, inbound.RequestId, keepUntil, Encoding.UTF8.GetString(RemoteCommandAnswer.Serialize(
            new AppCommandResponse(inbound.RequestId, AppCommandStatus.Accepted, AppCommandExit.Done,
                "The command was accepted; its outcome was not recorded."))));
        await _store.SaveRingAsync(_ring, cancellationToken).ConfigureAwait(false);

        var command = new AppCommandRequest(entry.Name, words, inbound.RequestId) { Origin = RemoteOrigin };
        var response = await _execute(command, cancellationToken).ConfigureAwait(false);
        await FinishAsync(inbound, controllerName, request, riskClass, response, optionNames, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>origin</c> of a request that came from another device; the local log line names it.</summary>
    internal const string RemoteOrigin = "remote";

    /// <summary>Records the answer in the ring (when the request was read), replies, and writes the log line.</summary>
    private async Task FinishAsync(RemoteCommandInbound inbound, string controllerName, RemoteCommandRequest? request,
        string riskClass, AppCommandResponse response, IReadOnlyList<string>? optionNames, CancellationToken cancellationToken)
    {
        var answer = RemoteCommandAnswer.Serialize(response with { RequestId = inbound.RequestId });
        if (request is not null)
        {
            _ring.Record(inbound.ControllerId, inbound.RequestId, RemoteCommandWindow.KeepUntil(request.IssuedAt, request.TtlSeconds),
                Encoding.UTF8.GetString(answer));
            await _store.SaveRingAsync(_ring, cancellationToken).ConfigureAwait(false);
        }

        await inbound.ReplyAsync(answer).ConfigureAwait(false);
        await AddLogAsync(new RemoteCommandLogEntry(DateTimeOffset.UtcNow, controllerName, inbound.ControllerId, true,
            request?.Verb ?? string.Empty, riskClass, response.Reason ?? response.Status, response.ExitCode,
            RemoteCommandLog.RedactedArguments(optionNames ?? [])), cancellationToken).ConfigureAwait(false);
    }

    private async Task AddLogAsync(RemoteCommandLogEntry entry, CancellationToken cancellationToken)
    {
        lock (_logGate)
        {
            _commandLog.Add(entry);
        }

        _log?.Event("REMOTE COMMAND",
            $"verb={(entry.Verb.Length > 0 ? entry.Verb.Replace(' ', '-') : "none")}",
            $"class={(entry.RiskClass.Length > 0 ? entry.RiskClass : "none")}",
            $"outcome={entry.Outcome}",
            $"exit={entry.Exit}",
            $"verified={entry.Verified}",
            $"args={entry.Arguments.Count}");
        await _store.SaveLogAsync(_commandLog, cancellationToken).ConfigureAwait(false);
        LogChanged?.Invoke();
    }
}
