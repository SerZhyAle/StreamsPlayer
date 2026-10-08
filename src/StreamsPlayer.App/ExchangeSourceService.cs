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
    internal ExchangeAccount Account { get; private set; } = new(ExchangeProtocol.NewDeviceId());
    internal string StatusKey { get; private set; } = "ExchangeOff";
    internal string? PreviousFingerprint { get; private set; }
    internal string? PresentedFingerprint { get; private set; }
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
            var pin = host == Account.Host && port == Account.Port ? Account.Pin : null;
            using var connection = await ExchangeTlsConnection.OpenAsync(host, port, pin, deadline.Token).ConfigureAwait(false);
            return connection.Fingerprint;
        }
        catch (ExchangeCertificateException exception)
        {
            ShowCertificate(exception);
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

            await StopLoopAsync().ConfigureAwait(false);
            SetStatus("ExchangeConnecting");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pin = acceptedPin ?? (host == Account.Host && port == Account.Port ? Account.Pin : null);
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
            PreviousFingerprint = PresentedFingerprint = null;
            StartLoop(connection, interval);
            connection = null;
        }
        catch (ExchangeCertificateException exception)
        {
            ShowCertificate(exception);
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
            await StopLoopAsync().ConfigureAwait(false);
            if (fingerprint != PresentedFingerprint || Account.Token is null)
            {
                throw new InvalidOperationException("No pending certificate replacement.");
            }

            var updated = Account with { Pin = fingerprint };
            _store.Save(updated);
            Account = updated;
            PreviousFingerprint = PresentedFingerprint = null;
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
            PreviousFingerprint = PresentedFingerprint = null;
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

    private void StartLoop(ExchangeTlsConnection? enrolled, int interval)
    {
        _connectionCancellation = new CancellationTokenSource();
        _connectionTask = RunAsync(enrolled, interval, _connectionCancellation.Token);
    }

    private async Task StopLoopAsync()
    {
        if (_connectionCancellation is null)
        {
            return;
        }

        await _connectionCancellation.CancelAsync().ConfigureAwait(false);
        await _connectionTask.ConfigureAwait(false);
        _connectionCancellation.Dispose();
        _connectionCancellation = null;
    }

    private async Task RunAsync(ExchangeTlsConnection? connection, int interval, CancellationToken cancellationToken)
    {
        var backoffSeconds = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (connection is null)
                {
                    SetStatus("ExchangeConnecting");
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
                ShowCertificate(exception);
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
        await ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type = "keepalive" }, true, cancellationToken).ConfigureAwait(false);
        // SP-0201 requirement 1: the list fills the view first, then subscribe keeps it current. A gap in
        // revision below answers itself with a new list; subscribe is written once, after the first answer.
        await ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type = "list" }, true, cancellationToken).ConfigureAwait(false);
        var subscribed = false;
        Task<JsonDocument>? pending = null;
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nextTick = Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                pending ??= ExchangeProtocol.ReadAsync(connection.Stream, true, readCancellation.Token);
                if (await Task.WhenAny(pending, nextTick).ConfigureAwait(false) == nextTick)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lastAnswer.Elapsed.TotalSeconds >= interval * 3)
                    {
                        throw new IOException("Exchange keepalive window expired.");
                    }

                    await ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type = "keepalive" }, true, cancellationToken).ConfigureAwait(false);
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
                            await ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type = "subscribe" }, true, cancellationToken).ConfigureAwait(false);
                            subscribed = true;
                        }

                        _directory.ApplyFull(response.RootElement);
                        PublishDirectory();
                        break;
                    case "changed":
                        if (_directory.ApplyChange(response.RootElement).RelistNeeded)
                        {
                            await ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type = "list" }, true, cancellationToken).ConfigureAwait(false);
                        }

                        PublishDirectory();
                        break;
                    case "cast-offer":
                        await HandleCastOfferAsync(connection, response.RootElement, cancellationToken).ConfigureAwait(false);
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
            _castCoordinator.CancelAll();
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

    private async Task HandleCastOfferAsync(ExchangeTlsConnection connection, JsonElement root, CancellationToken cancellationToken)
    {
        if (!ExchangeCastOffer.TryParse(root, out var offer) || offer is null)
        {
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

        if (offer.Support != ExchangeBroadcastSupport.Supported || offer.Descriptor is null)
        {
            await ExchangeProtocol.WriteAsync(connection.Stream, ExchangeCastAnswer.Unsupported(offer.CastId), true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Account.AutoAcceptCasts)
        {
            await ExchangeProtocol.WriteAsync(connection.Stream, ExchangeCastAnswer.Accept(offer.CastId), true, cancellationToken).ConfigureAwait(false);
            CastAccepted?.Invoke(offer);
            return;
        }

        if (CastPromptRequested is { } promptHandler)
        {
            var accepted = await _castCoordinator.RequestDecisionAsync(
                offer,
                (off, ct) => promptHandler(off, ct),
                cancellationToken).ConfigureAwait(false);

            if (accepted)
            {
                await ExchangeProtocol.WriteAsync(connection.Stream, ExchangeCastAnswer.Accept(offer.CastId), true, cancellationToken).ConfigureAwait(false);
                CastAccepted?.Invoke(offer);
            }
            else
            {
                await ExchangeProtocol.WriteAsync(connection.Stream, ExchangeCastAnswer.Decline(offer.CastId), true, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ExchangeProtocol.WriteAsync(connection.Stream, ExchangeCastAnswer.Decline(offer.CastId), true, cancellationToken).ConfigureAwait(false);
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

    private static bool IsConnectionFailure(Exception exception) => exception is IOException or SocketException
        or AuthenticationException or OperationCanceledException or JsonException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException;

    private static async Task TryByeAsync(ExchangeTlsConnection connection)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await ExchangeProtocol.WriteAsync(connection.Stream, new { schemaVersion = 2, type = "bye" }, true, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception) || exception is ObjectDisposedException)
        {
            // A failed or revoked stream has already gone offline; quitting must still complete.
        }
    }

    private void ShowCertificate(ExchangeCertificateException exception)
    {
        PreviousFingerprint = exception.Previous;
        PresentedFingerprint = exception.Presented;
        SetStatus("ExchangeCertificate");
    }

    private void SetStatus(string key)
    {
        StatusKey = key;
        Changed?.Invoke();
    }
}
