using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed class ExchangeRetryRefusalException(string key) : IOException
{
    internal string Key { get; } = key;
}

internal sealed class ExchangeSourceService
{
    private readonly ExchangeAccountStore _store;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CancellationTokenSource? _connectionCancellation;
    private Task _connectionTask = Task.CompletedTask;
    private bool _storageUnavailable;
    private readonly ExchangeDirectoryState _directory = new();
    private readonly ExchangeCastCoordinator _castCoordinator = new();
    private const int MaximumOpenCastOffers = 8;
    internal ExchangeAccount Account { get; private set; } = new(ExchangeProtocol.NewDeviceId());
    internal string StatusKey { get; private set; } = "ExchangeOff";
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

    internal ExchangeSourceService(string directory)
    {
        _store = new ExchangeAccountStore(directory);
        try
        {
            Account = _store.Load();
            _store.Save(Account);
            StatusKey = _store.ProtectionFailed ? "ExchangeEnrollAgain" : "ExchangeOff";
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

    internal async Task<string?> InspectCertificateAsync(string host, int port)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
        {
            SetStatus("ExchangeInvalidEntry");
            return null;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pin = IsAccountEndpoint(host, port) ? Account.Pin : null;
            using var connection = await ExchangeTlsConnection.OpenAsync(host, port, pin, deadline.Token).ConfigureAwait(false);
            return connection.Fingerprint;
        }
        catch (ExchangeCertificateException exception)
        {
            // The status line describes the account's own connection, so a probe of another server only
            // returns its fingerprint to the enrollment that asked; it records nothing about the account.
            if (IsAccountEndpoint(host, port))
            {
                ShowCertificate(exception, host, port);
            }

            return exception.Presented;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            SetStatus("ExchangeUnavailable");
            return null;
        }
    }

    internal async Task EnrollAsync(string host, int port, string login, bool pairingCode, char[] secret, string? acceptedPin)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        ExchangeTlsConnection? connection = null;
        var savingAccount = false;
        var resumePrevious = false;
        try
        {
            EnsureStorage();
            login = login.ToLowerInvariant();
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535
                || login.Length is < 3 or > 64 || login.Any(character =>
                    character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '_' and not '-')
                || secret.Length == 0)
            {
                SetStatus("ExchangeInvalidEntry");
                return;
            }

            // The running receiver is stopped so the new enrollment owns the one control connection; any exit
            // that does not replace the account puts it back (the finally below).
            resumePrevious = _connectionCancellation is not null && Account.Enabled && Account.Token is not null;
            await StopLoopAsync().ConfigureAwait(false);
            SetStatus("ExchangeConnecting");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pin = acceptedPin ?? (IsAccountEndpoint(host, port) ? Account.Pin : null);
            connection = await ExchangeTlsConnection.OpenAsync(host, port, pin, deadline.Token).ConfigureAwait(false);
            await ExchangeProtocol.WriteEnrollmentAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "enroll", login, deviceId = Account.DeviceId,
                deviceName = "STREAMS Player", product = "streamsplayer-windows", productVersion = ProductInfo.Version,
                platform = "windows", roles = new[] { "receiver" }, receiver = ExchangeCapabilities.Receiver,
                keepaliveSeconds = 30
            }, pairingCode ? "pairingCode" : "password", secret, deadline.Token).ConfigureAwait(false);
            using var response = await ExchangeHandshake.ReadAsync(connection.Stream, "enrolled", deadline.Token).ConfigureAwait(false);
            var root = response.RootElement;
            if (ExchangeProtocol.String(root, "type") == "refused")
            {
                var refusalKey = ExchangeProtocol.RefusalKey(ExchangeProtocol.String(root, "reason"));
                SetStatus(refusalKey);
                return;
            }

            var token = ExchangeProtocol.String(root, "deviceToken");
            if (token is null || token.Length != 43 || token.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new InvalidDataException("Invalid device token.");
            }

            var interval = ExchangeHandshake.ReadInterval(root);
            var enrolled = new ExchangeAccount(Account.DeviceId, host, port, login, token, connection.Fingerprint, true);
            savingAccount = true;
            _store.Save(enrolled);
            savingAccount = false;
            Account = enrolled;
            ClearPendingCertificate();
            resumePrevious = false;
            StartLoop(connection, interval);
            connection = null;
        }
        catch (ExchangeCertificateException exception)
        {
            ShowCertificate(exception, host, port);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            SetStatus(savingAccount || _storageUnavailable || exception is UnauthorizedAccessException
                ? "ExchangeStorageFailed" : "ExchangeUnavailable");
        }
        finally
        {
            Array.Clear(secret);
            connection?.Dispose();
            if (resumePrevious && _connectionCancellation is null)
            {
                // The refusal or failure stays on the status line until the receiver is back, not replaced
                // by "connecting" the moment it starts again.
                StartLoop(null, 0, announceConnecting: false);
            }

            _operations.Release();
        }
    }

    internal async Task SetEnabledAsync(bool enabled)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
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

            await StopLoopAsync().ConfigureAwait(false);
            var updated = Account with { Pin = fingerprint };
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
            EnsureStorage();
            await StopLoopAsync().ConfigureAwait(false);
            var forgotten = new ExchangeAccount(ExchangeProtocol.NewDeviceId());
            _store.Save(forgotten);
            Account = forgotten;
            ClearPendingCertificate();
            SetStatus("ExchangeForgotten");
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
                    connection = await ExchangeTlsConnection.OpenAsync(Account.Host, Account.Port, Account.Pin, deadline.Token).ConfigureAwait(false);
                    await ExchangeProtocol.WriteAsync(connection.Stream, new
                    {
                        schemaVersion = 2, type = "hello", deviceId = Account.DeviceId,
                        deviceToken = Account.Token, productVersion = ProductInfo.Version, keepaliveSeconds = 30
                    }, false, deadline.Token).ConfigureAwait(false);
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
                SetStatus("ExchangeUnavailable");
                ClearDirectory();
            }
            finally
            {
                if (connection is not null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await TryByeAsync(connection).ConfigureAwait(false);
                    }

                    connection.Dispose();
                    connection = null;
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
            }
            finally
            {
                writeGate.Release();
            }
        }

        await SendAsync(new { schemaVersion = 2, type = "keepalive" }, cancellationToken).ConfigureAwait(false);
        // SP-0201 requirement 1: the list fills the view first, then subscribe keeps it current. A gap in
        // revision below answers itself with a new list; subscribe is written once, after the first answer.
        await SendAsync(new { schemaVersion = 2, type = "list" }, cancellationToken).ConfigureAwait(false);
        var subscribed = false;
        Task<JsonDocument>? pending = null;
        var castTasks = new List<Task>();
        var castFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var castCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                pending ??= ExchangeProtocol.ReadAsync(connection.Stream, true, readCancellation.Token);
                var ready = await Task.WhenAny(pending, nextTick, castFailed.Task).ConfigureAwait(false);
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
                        if (!subscribed)
                        {
                            await SendAsync(new { schemaVersion = 2, type = "subscribe" }, cancellationToken).ConfigureAwait(false);
                            subscribed = true;
                        }

                        _directory.ApplyFull(response.RootElement);
                        PublishDirectory();
                        break;
                    case "changed":
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
                            castTasks.RemoveAll(task => task.IsCompleted);
                            // A sender in a loop cannot grow this without bound: an offer past the cap goes
                            // unanswered and the server times it out for the caster.
                            if (castTasks.Count < MaximumOpenCastOffers)
                            {
                                castTasks.Add(AnswerCastOfferAsync(offer, SendAsync, castFailed, castCancellation.Token));
                            }
                        }

                        break;
                    case "cast-stop":
                        HandleCastStop(response.RootElement);
                        break;
                    case "refused":
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
            // writes to the stream after the connection is disposed. They never throw (AnswerCastOfferAsync).
            _castCoordinator.CancelAll();
            await castCancellation.CancelAsync().ConfigureAwait(false);
            foreach (var task in castTasks)
            {
                await ObserveAsync(task).ConfigureAwait(false);
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
        Directory = ExchangeDirectoryState.HideOwnDevice(_directory.Snapshot, Account.DeviceId);
        DirectoryChanged?.Invoke();
    }

    private void ClearDirectory()
    {
        Directory = null;
        DirectoryChanged?.Invoke();
    }

    /// <summary>
    /// SP-0205 / DEVICE-EXCHANGE 7.8, run off the control stream's read loop: the question to the user can take
    /// as long as the offer window, and a loop that waits for it stops answering keepalives and reading
    /// frames. A repeated offer for the same broadcast folds into the open question in the coordinator.
    /// </summary>
    private async Task AnswerCastOfferAsync(ExchangeCastOffer offer, Func<object, CancellationToken, Task> send,
        TaskCompletionSource castFailed, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(offer.DeviceName) && Directory is { } dir)
            {
                var matchedGroup = dir.Groups.FirstOrDefault(g => g.DeviceId == offer.DeviceId);
                if (matchedGroup is not null && !string.IsNullOrWhiteSpace(matchedGroup.DeviceName))
                {
                    offer = offer with { DeviceName = matchedGroup.DeviceName };
                }
            }

            if (offer.Support != ExchangeBroadcastSupport.Supported || offer.Descriptor is null)
            {
                await send(ExchangeCastAnswer.Unsupported(offer.BroadcastId, offer.CastId), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (Account.AutoAcceptCasts)
            {
                await send(ExchangeCastAnswer.Accept(offer.BroadcastId, offer.CastId), cancellationToken).ConfigureAwait(false);
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
        if (reason is "device-revoked" or "bad-credentials")
        {
            var cleared = Account with { Token = null, Enabled = false, DeviceId = ExchangeProtocol.NewDeviceId() };
            Account = cleared;
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
    private static bool IsConnectionFailure(Exception exception) => exception is IOException or SocketException
        or AuthenticationException or OperationCanceledException or JsonException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException or InvalidDataException or ObjectDisposedException;

    private static async Task TryByeAsync(ExchangeTlsConnection connection)
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
        Account.Host.Length > 0 && port == Account.Port && string.Equals(host, Account.Host, StringComparison.OrdinalIgnoreCase);

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

        SetStatus("ExchangeCertificate");
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
}
