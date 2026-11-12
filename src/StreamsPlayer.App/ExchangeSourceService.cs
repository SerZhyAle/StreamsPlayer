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
