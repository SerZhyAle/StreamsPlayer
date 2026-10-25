using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed class ExchangeRetryRefusalException(string key) : IOException
{
    internal string Key { get; } = key;
}

internal sealed partial class ExchangeSourceService
{
    private readonly ExchangeAccountStore _store;
    private readonly ExchangeTrustBook _trustBook;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CancellationTokenSource? _connectionCancellation;
    private Task _connectionTask = Task.CompletedTask;
    private bool _storageUnavailable;
    private readonly ExchangeDirectoryState _directory = new();
    private readonly ExchangeCastCoordinator _castCoordinator = new();
    // Bounds on what one control stream can make this device hold open. Distinct broadcasts waiting on the
    // user are few by nature (one prompt is on screen at a time); repeats of one broadcast and the answers
    // that need no question (unsupported, auto-accept) are counted apart, so neither can crowd out the other.
    private const int MaximumPendingCastBroadcasts = 8;
    private const int MaximumAnswersPerBroadcast = 4;
    private const int MaximumLiveCastAnswers = 64;

    private readonly record struct OpenCastAnswer(string BroadcastId, bool AwaitsUser, Task Task);
    internal ExchangeAccount Account { get; private set; } = new(ExchangeProtocol.NewDeviceId());
    internal string StatusKey { get; private set; } = "ExchangeOff";

    /// <summary>
    /// True only while the Settings page has the account both registered (it holds a sign-in) and switched on.
    /// This is the one question every control that exists for the exchange server asks before it is shown: with
    /// the account off or absent the server is not part of the person's UI, and Settings is where it comes back.
    /// </summary>
    internal bool IsActive => Account.Enabled && Account.Token is not null;

    /// <summary>
    /// The localized key of how the last enrollment attempt ended, when it did not succeed. It is a separate
    /// line from <see cref="StatusKey"/> because a refused enrollment puts the previous receiver back, and
    /// that receiver's own "online" would otherwise replace the refusal within a second - a failed
    /// enrollment would read as a successful one. It stays until the user's next action on the account
    /// (enroll, enable or disable, trust, forget).
    /// </summary>
    internal string? EnrollmentOutcomeKey { get; private set; }

    /// <summary>
    /// SP-0225: the server the last sign-in attempt or certificate probe addressed. A failed attempt leaves no account,
    /// so this is what tells the connection check that <see cref="EnrollmentOutcomeKey"/> is about the server the form names.
    /// </summary>
    internal (string Host, int Port)? LastAttemptEndpoint { get; private set; }

    // The pending certificate replacement of the account's own server. Both are set only for a fingerprint
    // read from the account's host and port (_presentedHost/_presentedPort), never for another server.
    internal string? PreviousFingerprint { get; private set; }
    internal string? PresentedFingerprint { get; private set; }
    private string? _presentedHost;
    private int _presentedPort;
    internal event Action? Changed;

    // SP-0201: the account's live directory, with this device's own records hidden. Null while offline -
    // a lost connection is not a broadcast ending, so the view empties but nothing is marked ended; the
    // full list that reopens the stream is what decides ends by absence.
    internal ExchangeDirectorySnapshot? Directory { get; private set; }
    internal event Action? DirectoryChanged;

    // SP-0205: Cast offer and stop events over the control stream
    internal event Func<ExchangeCastOffer, CancellationToken, Task<bool>>? CastPromptRequested;
    internal event Action<ExchangeCastOffer>? CastAccepted;
    internal event Action<ExchangeCastStop>? CastStopped;

    private readonly Action<string>? _diagnostic;
    private string? _lastEnvelopeType;

    internal ExchangeSourceService(string directory, Action<string>? diagnostic = null)
    {
        _diagnostic = diagnostic;
        _store = new ExchangeAccountStore(directory);
        _trustBook = new ExchangeTrustBook(directory);
        Guests = new ExchangeGuestEntries(directory);
        try
        {
            Account = _store.Load();
            // SP-0212 (L9): an account read from the file is already stored, byte for byte; re-saving it on
            // every launch meant a transient sharing violation at that redundant write marked storage
            // unavailable for the whole session. Only a freshly created device id needs its first write.
            if (_store.CreatedFresh)
            {
                _store.Save(Account);
            }
            StatusKey = _store.ProtectionFailed ? "ExchangeEnrollAgain"
                : Account.SignedOut ? "ExchangeSignedOut" : "ExchangeOff";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            _storageUnavailable = true;
            StatusKey = "ExchangeStorageFailed";
        }
    }

    internal void Start()
    {
        if (!_storageUnavailable && _connectionCancellation is null && Account.Enabled && Account.Token is not null)
        {
            StartLoop(null, 0);
        }
    }

    internal async Task<string?> InspectCertificateAsync(string host, int port, string? requiredPin = null,
        CancellationToken cancellationToken = default) =>
        (await ProbeCertificateAsync(host, port, requiredPin, cancellationToken).ConfigureAwait(false))?.Fingerprint;

    /// <summary>
    /// <see cref="InspectCertificateAsync"/> with what the sign-in surface needs to decide on a prompt: an imported
    /// string's <c>fp</c> that replaces a pin the own entry already holds is a changed certificate (item BN).
    /// </summary>
    internal async Task<ExchangeProbedLeaf?> ProbeCertificateAsync(string host, int port, string? requiredPin = null,
        CancellationToken cancellationToken = default)
    {
        // The probe is the first step of an enrollment, so it starts a new attempt: the last attempt's
        // outcome is not the answer to this one.
        EnrollmentOutcomeKey = null;
        LastAttemptEndpoint = (ExchangeAccountIdentity.NormalizeHost(host), port);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
        {
            ReportOutcome("ExchangeInvalidEntry");
            return null;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var pin = requiredPin ?? (IsAccountEndpoint(host, port) ? Account.Pin : null);
            // A renewal that keeps the entry's key (item AY) opens with no prompt, so the probe carries the key too.
            var pinKey = requiredPin is null && IsAccountEndpoint(host, port) ? Account.PinKey : null;
            using var connection = await ExchangeTlsConnection.OpenAsync(host, port, pin, deadline.Token, pinKey).ConfigureAwait(false);
            return new ExchangeProbedLeaf(connection.Fingerprint, requiredPin is not null
                && ReplacesHeldPin(host, port, connection.Fingerprint, connection.PublicKeyFingerprint));
        }
        catch (ExchangeCertificateException exception)
        {
            if (requiredPin is not null)
            {
                ReportOutcome("ExchangeStringCertificateMismatch");
                throw;
            }
            // The status line describes the account's own connection, so a probe of another server only
            // returns its fingerprint to the enrollment that asked; it records nothing about the account.
            if (IsAccountEndpoint(host, port))
            {
                ShowCertificate(exception, host, port);
            }

            return new ExchangeProbedLeaf(exception.Presented, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReportOutcome("ExchangeJoinCancelled");
            return null;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // The probe never touched the account's receiver, so its connection line is left as it is. No
            // connection was open, so a closed stream cannot be "no answer" here (item AN).
            ReportOutcome(ExchangeFailures.KeyOf(exception, connectionOpen: false));
            return null;
        }
    }

    internal async Task SetEnabledAsync(bool enabled)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            EnrollmentOutcomeKey = null;
            EnsureStorage();
            await StopLoopAsync().ConfigureAwait(false);
            var updated = Account with { Enabled = enabled && Account.Token is not null };
            _store.Save(updated);
            Account = updated;
            if (updated.Enabled)
            {
                StartLoop(null, 0);
            }
            else
            {
                SetStatus("ExchangeOff");
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    internal async Task SetAutoAcceptCastsAsync(bool autoAccept)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureStorage();
            var updated = Account with { AutoAcceptCasts = autoAccept };
            _store.Save(updated);
            Account = updated;
            Changed?.Invoke();
        }
        finally
        {
            _operations.Release();
        }
    }

    internal async Task AcceptReplacementPinAsync(string fingerprint)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureStorage();
            // Checked before the receiver is stopped, and against the endpoint the fingerprint was read from:
            // a refused call must leave the live connection alone, and a fingerprint is only ever a
            // replacement for the pin of the server it came from.
            if (fingerprint != PresentedFingerprint || Account.Token is null || _presentedHost is null
                || !IsAccountEndpoint(_presentedHost, _presentedPort))
            {
                throw new InvalidOperationException("No pending certificate replacement.");
            }

            EnrollmentOutcomeKey = null;
            await StopLoopAsync().ConfigureAwait(false);
            // The key kept with the old pin belongs to the server the user is replacing: it is dropped, so a leaf
            // with that key is not accepted any more, and the next connection records the new leaf's key.
            var updated = Account with { Pin = fingerprint, PinKey = null };
            _store.Save(updated);
            Account = updated;
            ClearPendingCertificate();
            if (updated.Enabled)
            {
                StartLoop(null, 0);
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    internal async Task ForgetAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            EnrollmentOutcomeKey = null;
            EnsureStorage();
            await LeaveAccountAsync().ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    internal async Task StopAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
            // S7-6: keys a cast task or a guest viewer trusted since the last directory are written before the process ends.
            _trustBook.SaveIfDirty();
        }
        finally
        {
            _operations.Release();
        }
    }

    private void EnsureStorage()
    {
        if (_storageUnavailable)
        {
            throw new IOException("Exchange account storage is unavailable.");
        }
    }

    private void StartLoop(ExchangeTlsConnection? enrolled, int interval, bool announceConnecting = true)
    {
        _connectionCancellation = new CancellationTokenSource();
        _connectionTask = RunAsync(enrolled, interval, announceConnecting, _connectionCancellation.Token);
    }

    private async Task StopLoopAsync()
    {
        if (_connectionCancellation is null)
        {
            return;
        }

        await _connectionCancellation.CancelAsync().ConfigureAwait(false);
        // Teardown must never be blocked by how the loop ended: a loop that faulted would otherwise rethrow
        // here on every Forget, Disable and Enroll until the process restarts. The loop classifies the
        // failures it expects itself; an unexpected one ends it with the status stale, so say so.
        await ObserveAsync(_connectionTask).ConfigureAwait(false);
        if (_connectionTask.IsFaulted)
        {
            ClearDirectory();
            SetStatus("ExchangeUnavailable");
        }

        _connectionCancellation.Dispose();
        _connectionCancellation = null;
        _connectionTask = Task.CompletedTask;
    }

    /// <summary>Waits for a task to end by any outcome without rethrowing, and marks a fault as observed.</summary>
    private static async Task ObserveAsync(Task task)
    {
        await Task.WhenAny(task).ConfigureAwait(false);
        _ = task.Exception;
    }

    private async Task RunAsync(ExchangeTlsConnection? connection, int interval, bool announceConnecting,
        CancellationToken cancellationToken)
    {
        var backoffSeconds = 1;
        var authenticated = connection is not null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (connection is null)
                {
                    if (announceConnecting)
                    {
                        SetStatus("ExchangeConnecting");
                    }

                    announceConnecting = true;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    connection = await ExchangeTlsConnection.OpenAsync(Account.Host, Account.Port, Account.Pin, deadline.Token,
                        Account.PinKey).ConfigureAwait(false);
                    await ExchangeProtocol.WriteAsync(connection.Stream, new
                    {
                        schemaVersion = 2, type = "hello", deviceId = Account.DeviceId,
                        deviceToken = Account.Token, productVersion = ProductInfo.Version, keepaliveSeconds = 30
                    }, false, deadline.Token).ConfigureAwait(false);
                    _lastEnvelopeType = "hello";
                    using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", deadline.Token).ConfigureAwait(false);
                    if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
                    {
                        if (EndForRefusal(welcome.RootElement))
                        {
                            return;
                        }

                        throw new ExchangeRetryRefusalException(ExchangeProtocol.RefusalKey(ExchangeProtocol.String(welcome.RootElement, "reason")));
                    }

                    interval = ExchangeHandshake.ReadInterval(welcome.RootElement);
                    // The kind is read before anything announces this session: a guest's welcome must never reach
                    // the producer's SessionOnline, which re-publishes broadcasts (item AO, item H).
                    var kind = ExchangeAccountKinds.Parse(ExchangeProtocol.String(welcome.RootElement, "accountKind"));
                    if (kind == ExchangeAccountKind.Guest && await AccountBecameGuestAsync().ConfigureAwait(false))
                    {
                        return;
                    }

                    if (kind == ExchangeAccountKind.Member)
                    {
                        AccountBecameMember();
                    }

                    RememberLeaf(connection);
                    RememberServerFacts(welcome.RootElement);
                    authenticated = true;
                }

                SetStatus("ExchangeOnline");
                await HoldAsync(connection, interval, cancellationToken).ConfigureAwait(false);
                ClearDirectory();
                return;
            }
            catch (ExchangeCertificateException exception)
            {
                ShowCertificate(exception, Account.Host, Account.Port);
                ClearDirectory();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ClearDirectory();
                return;
            }
            catch (ExchangeRetryRefusalException exception)
            {
                SetStatus(exception.Key);
                ClearDirectory();
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                // A connection that was open when it failed closed with no frame after this product's own
                // envelope (item AN); one that never opened could not be reached, or could not be protected.
                SetStatus(ExchangeFailures.KeyOf(exception, connectionOpen: connection is not null));
                if (connection is not null)
                {
                    _diagnostic?.Invoke($"EXCHANGE CLOSED code=unanswered envelope={_lastEnvelopeType ?? "unknown"}");
                }
                ClearDirectory();
            }
            finally
            {
                if (connection is not null)
                {
                    if (_accountBeingRemoved is { } removing)
                    {
                        if (authenticated)
                        {
                            _removalWritten = await TryRevokeAsync(connection, removing.DeviceId).ConfigureAwait(false);
                        }
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        await TryByeAsync(connection).ConfigureAwait(false);
                    }

                    connection.Dispose();
                    connection = null;
                    authenticated = false;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoffSeconds = Math.Min(30, backoffSeconds * 2);
        }
    }

    private async Task HoldAsync(ExchangeTlsConnection connection, int interval, CancellationToken cancellationToken)
    {
        var lastAnswer = System.Diagnostics.Stopwatch.StartNew();
        // The read loop and the cast answers share one stream, and a frame is several writes: they take turns.
        using var writeGate = new SemaphoreSlim(1, 1);
        async Task SendAsync(object envelope, CancellationToken token)
        {
            await writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await ExchangeProtocol.WriteAsync(connection.Stream, envelope, true, token).ConfigureAwait(false);
                _lastEnvelopeType = ExchangeProtocol.String(JsonSerializer.SerializeToElement(envelope, ExchangeProtocol.JsonOptions), "type");
            }
            finally
            {
                writeGate.Release();
            }
        }

        await SendAsync(new { schemaVersion = 2, type = "keepalive" }, cancellationToken).ConfigureAwait(false);
        // SP-0222 requirement 1: nothing is listed because a session came up. A list is asked for by a lease
        // (ExchangeSourceService.Directory.cs): the list fills the view first, then subscribe keeps it current.
        // A gap in revision below answers itself with a new list; subscribe is written once per session, after
        // the first answer a follower asked for.
        var subscribed = false;
        Task<JsonDocument>? pending = null;
        var castTasks = new List<OpenCastAnswer>();
        var castFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var castCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
        // SP-0217: the producer's envelopes ride the same stream the loop owns. The queue is bounded and
        // refuses at once when full, so a broadcast start can never be queued past the session it needs.
        var outgoing = Channel.CreateBounded<(object Envelope, TaskCompletionSource<bool> Written)>(32);
        _producerOutgoing = outgoing;
        Task<bool> outgoingWait = outgoing.Reader.WaitToReadAsync(readCancellation.Token).AsTask();
        try
        {
            // A lease taken while no session was up (or in the gap before the queue above existed) is owed its
            // list now. Checked after the queue is published: a lease taken from here on posts its own.
            if (HoldsDirectory)
            {
                await SendAsync(new { schemaVersion = 2, type = "list" }, cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                pending ??= ExchangeProtocol.ReadAsync(connection.Stream, true, readCancellation.Token);
                var ready = await Task.WhenAny(pending, nextTick, castFailed.Task, outgoingWait).ConfigureAwait(false);
                if (ready == castFailed.Task)
                {
                    throw new IOException("Exchange cast answer could not be delivered.");
                }

                if (ready == nextTick)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lastAnswer.Elapsed.TotalSeconds >= interval * 3)
                    {
                        throw new IOException("Exchange keepalive window expired.");
                    }

                    await SendAsync(new { schemaVersion = 2, type = "keepalive" }, cancellationToken).ConfigureAwait(false);
                    nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                    continue;
                }

                if (ready == outgoingWait)
                {
                    // Drain everything the producer posted; one write per envelope, in order, and the
                    // writer's own completion says whether it landed.
                    while (outgoing.Reader.TryRead(out var posted))
                    {
                        try
                        {
                            await SendAsync(posted.Envelope, cancellationToken).ConfigureAwait(false);
                            posted.Written.TrySetResult(true);
                        }
                        catch (Exception exception) when (IsConnectionFailure(exception))
                        {
                            posted.Written.TrySetResult(false);
                            throw;
                        }
                    }

                    outgoingWait = outgoing.Reader.WaitToReadAsync(readCancellation.Token).AsTask();
                    continue;
                }

                using var response = await pending.ConfigureAwait(false);
                pending = null;
                ExchangeHandshake.ValidateSchema(response.RootElement);
                var type = ExchangeProtocol.String(response.RootElement, "type");
                switch (type)
                {
                    case "keepalive":
                        lastAnswer.Restart();
                        break;
                    case "directory":
                        // An answer nobody holds a lease for (it crossed the release) is read and dropped.
                        if (!HoldsDirectory)
                        {
                            break;
                        }

                        if (!subscribed && HoldsFollow)
                        {
                            await SendAsync(new { schemaVersion = 2, type = "subscribe" }, cancellationToken).ConfigureAwait(false);
                            subscribed = true;
                        }

                        _directory.ApplyFull(response.RootElement);
                        _directoryFresh = true;
                        PublishDirectory();
                        break;
                    case "changed":
                        // The subscription outlives the open list (the wire has no unsubscribe): a push nobody
                        // follows, or one that arrives before this lease's list answered, is read and dropped.
                        if (!HoldsFollow || !_directoryFresh)
                        {
                            break;
                        }

                        if (_directory.ApplyChange(response.RootElement).RelistNeeded)
                        {
                            await SendAsync(new { schemaVersion = 2, type = "list" }, cancellationToken).ConfigureAwait(false);
                        }

                        PublishDirectory();
                        break;
                    case "cast-offer":
                        // Parsed here, while the frame is alive; answered on its own task, never in this loop.
                        if (ExchangeCastOffer.TryParse(response.RootElement, out var offer) && offer is not null)
                        {
                            StartCastAnswer(castTasks, offer, SendAsync, castFailed, castCancellation.Token);
                        }

                        break;
                    case "cast-stop":
                        HandleCastStop(response.RootElement);
                        break;
                    case "open":
                        // A tunnel for this device's own broadcast: the producer bridges it on its own
                        // task, off this loop, and owns the per-broadcast bound (item L).
                        if (ExchangeTunnelOpen.TryParse(response.RootElement) is { } tunnel)
                        {
                            TunnelRequested?.Invoke(tunnel, castCancellation.Token);
                        }

                        break;
                    case "broadcast-started":
                        AnswerBroadcastStart(response.RootElement, null);
                        break;
                    case "cast-result":
                        if (ExchangeCastResult.TryParse(response.RootElement) is { } castResult)
                        {
                            CastResult?.Invoke(castResult);
                        }

                        break;
                    case "password-changed":
                        HandlePasswordChanged(response.RootElement);
                        break;
                    case "refused":
                        if (ExchangeProtocol.String(response.RootElement, "broadcastId") is { } refusedBroadcastId)
                        {
                            AnswerBroadcastStart(response.RootElement,
                                ExchangeProtocol.String(response.RootElement, "reason") ?? "refused");
                            break;
                        }

                        // A refusal while a password change is out is that change's answer, and never this device's revocation.
                        if (TryAnswerPasswordChange(ExchangeProtocol.String(response.RootElement, "reason")))
                        {
                            break;
                        }

                        // A refusal of a broadcast start or update names no broadcast (7.4); it is that request's answer,
                        // not the end of a session that has nothing wrong with it.
                        if (TryAnswerBroadcastRefusal(ExchangeProtocol.String(response.RootElement, "reason")))
                        {
                            break;
                        }

                        if (EndForRefusal(response.RootElement))
                        {
                            return;
                        }

                        throw new ExchangeRetryRefusalException(ExchangeProtocol.RefusalKey(ExchangeProtocol.String(response.RootElement, "reason")));
                    default:
                        if (ExchangeProtocol.IsKnownType(type)
                            && type is not "pairing-code")
                        {
                            throw new InvalidDataException("Invalid exchange receiver control state.");
                        }

                        break;
                }
            }
        }
        finally
        {
            // The session is over: dismiss every open question and wait for the answer tasks, so none of them
            // writes to the stream after the connection is disposed. AnswerCastOfferAsync ends every failure
            // itself, and ObserveAsync waits for each by any outcome.
            _castCoordinator.CancelAll();
            await castCancellation.CancelAsync().ConfigureAwait(false);
            foreach (var open in castTasks)
            {
                await ObserveAsync(open.Task).ConfigureAwait(false);
            }

            // A password change still out when the session ends is over for whoever waits: its answer, if the
            // server ever wrote one, went with the stream. The next hello says the truth (item AT).
            AbandonPasswordChange();
            // The producer's queue ends with the session: an envelope that never reached the stream is
            // answered false, never left waiting for a stream that no longer exists.
            _producerOutgoing = null;
            Volatile.Write(ref _updateRefusalUntil, 0);
            outgoing.Writer.TryComplete();
            while (outgoing.Reader.TryRead(out var undelivered))
            {
                undelivered.Written.TrySetResult(false);
            }

            // Observe the single pending read before disposing the connection, including timeout and quit paths.
            await readCancellation.CancelAsync().ConfigureAwait(false);
            if (pending is not null)
            {
                try
                {
                    (await pending.ConfigureAwait(false)).Dispose();
                }
                catch (Exception exception) when (IsConnectionFailure(exception))
                {
                    // The owning loop handles the failure; this is only the cancelled outstanding read.
                }
            }
        }
    }

    private void PublishDirectory()
    {
        if (!HoldsDirectory)
        {
            return; // the last lease went while this frame was being read
        }

        ObserveDirectoryKeys(_directory.Snapshot);
        OpenDirectoryBoxes(_directory.Snapshot);
        Directory = ExchangeDirectoryState.HideOwnDevice(_directory.Snapshot, Account.DeviceId);
        DirectoryChanged?.Invoke();
    }

    private void ClearDirectory()
    {
        _directoryFresh = false;
        Directory = null;
        DirectoryChanged?.Invoke();
        // The session is over in every direction this method is reached from; the producer's own
        // state follows the same edge, so no broadcast keeps an exchange endpoint a dead stream gave it.
        PublicEndpoint = null;
        Features = [];
        SessionOffline?.Invoke();
    }

    /// <summary>
    /// SP-0205 / DEVICE-EXCHANGE 7.8, run off the control stream's read loop: the question to the user can take
    /// as long as the offer window, and a loop that waits for it stops answering keepalives and reading
    /// frames. A repeated offer for the same broadcast folds into the open question in the coordinator.
    /// </summary>
    private async Task AnswerCastOfferAsync(ExchangeCastOffer offer, Func<object, CancellationToken, Task> send,
        TaskCompletionSource castFailed, CancellationToken cancellationToken, bool refuseBusy = false)
    {
        var answered = false;
        try
        {
            if (refuseBusy)
            {
                // DEVICE-EXCHANGE item R's vocabulary has no "busy"; the receiver declining is the honest one.
                await send(ExchangeCastAnswer.Decline(offer.BroadcastId, castId: offer.CastId), cancellationToken).ConfigureAwait(false);
                answered = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(offer.DeviceName) && Directory is { } dir)
            {
                var matchedGroup = dir.Groups.FirstOrDefault(g => g.DeviceId == offer.DeviceId);
                if (matchedGroup is not null && !string.IsNullOrWhiteSpace(matchedGroup.DeviceName))
                {
                    offer = offer with { DeviceName = matchedGroup.DeviceName };
                }
            }

            // SP-0226 / item DK: a cast hands the record with its box; it is opened now, before the question or the
            // auto-accept, because the answer starts the playback and the playback asks for the keys.
            await OpenOfferBoxAsync(offer, cancellationToken).ConfigureAwait(false);
            if (offer.Support != ExchangeBroadcastSupport.Supported || offer.Descriptor is null)
            {
                await send(ExchangeCastAnswer.Unsupported(offer.BroadcastId, offer.CastId), cancellationToken).ConfigureAwait(false);
                answered = true;
                return;
            }

            if (Account.AutoAcceptCasts)
            {
                await send(ExchangeCastAnswer.Accept(offer.BroadcastId, offer.CastId), cancellationToken).ConfigureAwait(false);
                answered = true;
                CastAccepted?.Invoke(offer);
                return;
            }

            var decision = CastPromptRequested is { } promptHandler
                ? await _castCoordinator.RequestDecisionAsync(offer, promptHandler, cancellationToken).ConfigureAwait(false)
                : ExchangeCastDecision.Declined;
            if (cancellationToken.IsCancellationRequested)
            {
                // The session ended while the question was open; its stream is gone and no answer is owed.
                return;
            }

            await send(ExchangeCastAnswer.For(offer, decision), cancellationToken).ConfigureAwait(false);
            answered = true;
            if (decision == ExchangeCastDecision.Accepted)
            {
                CastAccepted?.Invoke(offer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The session ended; nothing to answer.
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // A cast answer that cannot be written means the stream is broken: hand that to the read loop,
            // which owns reconnecting, instead of letting an answer task fail where nobody looks.
            castFailed.TrySetResult();
        }
        catch (Exception) when (!answered)
        {
            // Whatever else went wrong - the prompt handler raising from the dialog or the dispatcher is the
            // known case - the caster is owed an answer, and an unanswered offer only ends at its 60 s window.
            // The exception is not logged here: this service has no log of its own, and the App's prompt
            // reports the faults it can attribute. After the answer went out there is nothing left to owe.
            await DeclineAfterFaultAsync(offer, send, castFailed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeclineAfterFaultAsync(ExchangeCastOffer offer, Func<object, CancellationToken, Task> send,
        TaskCompletionSource castFailed, CancellationToken cancellationToken)
    {
        try
        {
            await send(ExchangeCastAnswer.Decline(offer.BroadcastId, castId: offer.CastId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            castFailed.TrySetResult();
        }
    }

    /// <summary>
    /// Starts the answer task for one parsed offer, within what the stream may make this device hold open. A
    /// new broadcast past the pending cap is answered declined at once; repeats of an open broadcast past
    /// theirs are dropped (that broadcast's answer is already on its way), as is anything past the ceiling.
    /// None of it is logged: a flooding server is not worth a line per frame, and the payload stays out.
    /// </summary>
    private void StartCastAnswer(List<OpenCastAnswer> open, ExchangeCastOffer offer,
        Func<object, CancellationToken, Task> send, TaskCompletionSource castFailed, CancellationToken cancellationToken)
    {
        open.RemoveAll(item => item.Task.IsCompleted);
        if (open.Count >= MaximumLiveCastAnswers
            || open.Count(item => item.BroadcastId == offer.BroadcastId) >= MaximumAnswersPerBroadcast)
        {
            return;
        }

        var awaitsUser = offer.Support == ExchangeBroadcastSupport.Supported && offer.Descriptor is not null
            && !Account.AutoAcceptCasts;
        var refuseBusy = awaitsUser
            && !open.Any(item => item.AwaitsUser && item.BroadcastId == offer.BroadcastId)
            && open.Where(item => item.AwaitsUser).Select(item => item.BroadcastId).Distinct().Count()
                >= MaximumPendingCastBroadcasts;
        open.Add(new OpenCastAnswer(offer.BroadcastId, awaitsUser && !refuseBusy,
            AnswerCastOfferAsync(offer, send, castFailed, cancellationToken, refuseBusy)));
    }

    private void HandleCastStop(JsonElement root)
    {
        if (ExchangeCastStop.TryParse(root, out var stop) && stop is not null)
        {
            CastStopped?.Invoke(stop);
        }
    }

    private bool EndForRefusal(JsonElement response)
    {
        var reason = ExchangeProtocol.String(response, "reason");
        if (reason is "device-revoked" or "bad-credentials" or "signed-out")
        {
            // A password changed elsewhere (item AT) ends the sign-in and nothing else: the entry keeps its
            // server, login, pin, device id and attachments, and only the token is gone, so the person types the
            // new password and the same device enrolls again. A removed or unknown device starts over.
            ExchangeAccount cleared;
            if (reason == "signed-out")
            {
                cleared = Account with { Token = null, SignedOut = true };
            }
            else
            {
                // S7-8: the old id is gone for good, so the keys seen under it go with it (item DG).
                _trustBook.Forget(Account.DeviceId);
                // S7-9: a new id gets a new pair (item DF). Only clearing the old one would not do: the entry keeps its
                // host, so the next enrollment would take the new id for a known one and send no key at all.
                using var freshKey = ExchangeE2eKeyPair.Generate();
                cleared = (Account with { Token = null, Enabled = false, DeviceId = ExchangeProtocol.NewDeviceId(), SignedOut = false })
                    .WithE2eKey(freshKey);
            }

            Account = cleared;
            SignedOutNotice = reason == "signed-out";
            try
            {
                _store.Save(cleared);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
            {
                _storageUnavailable = true;
            }
        }

        SetStatus(ExchangeProtocol.RefusalKey(reason));
        return reason is not "rate-limited" and not "capacity" and not "unavailable";
    }

    // InvalidDataException is how Core reports a protocol or handshake violation (a bad frame length, a frame
    // that is not an object, a wrong schema, malformed welcome fields) and is not an IOException; an
    // ObjectDisposedException is the stream a failed read has already closed. Both mean "this connection is
    // finished", and the loop's answer to that is its backoff, not dying.
    internal static bool IsConnectionFailure(Exception exception) => exception is IOException or SocketException
        or AuthenticationException or OperationCanceledException or JsonException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException or InvalidDataException or ObjectDisposedException;

    internal static async Task TryByeAsync(ExchangeTlsConnection connection)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type = "bye" }, true, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // A failed or revoked stream has already gone offline; quitting must still complete.
        }
    }

    private bool IsAccountEndpoint(string host, int port) =>
        Account.Host.Length > 0 && ExchangeAccountIdentity.SameEndpoint(host, port, Account.Host, Account.Port);

    // The fingerprint is a replacement candidate only for the endpoint it was read from, and only when that
    // is the account's own: accepting one read from another server would pin that server's leaf onto this
    // account's host and break its connection.
    private void ShowCertificate(ExchangeCertificateException exception, string host, int port)
    {
        if (IsAccountEndpoint(host, port))
        {
            PreviousFingerprint = exception.Previous;
            PresentedFingerprint = exception.Presented;
            _presentedHost = host;
            _presentedPort = port;
        }

        SetStatus(ExchangeFailures.CertificateKey(exception));
    }

    private void ClearPendingCertificate()
    {
        PreviousFingerprint = PresentedFingerprint = null;
        _presentedHost = null;
        _presentedPort = 0;
    }

    private void SetStatus(string key)
    {
        StatusKey = key;
        Changed?.Invoke();
    }

    /// <summary>An attempt that ended without touching the receiver: only its outcome line changes.</summary>
    private void ReportOutcome(string key)
    {
        EnrollmentOutcomeKey = key;
        Changed?.Invoke();
    }

    /// <summary>
    /// An enrollment that failed after it had stopped the receiver. The connection line shows the failure
    /// until the receiver is back, and the outcome line keeps it after that.
    /// </summary>
    private void FailEnrollment(string key)
    {
        EnrollmentOutcomeKey = key;
        SetStatus(key);
    }
}
