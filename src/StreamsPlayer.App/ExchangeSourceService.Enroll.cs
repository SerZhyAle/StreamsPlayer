using System.IO;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>A leaf a certificate probe reached, and whether an imported string's <c>fp</c> would replace the entry's pin with it.</summary>
internal sealed record ExchangeProbedLeaf(string Fingerprint, bool ReplacesHeldPin);

internal sealed partial class ExchangeSourceService
{
    /// <summary>
    /// Item BN with item AY: a string's <c>fp</c> names a leaf, and it replaces the pin the own entry holds only when it
    /// is another leaf with another public key - that is a changed certificate, confirmed by the person with both
    /// fingerprints shown. The same key is a renewal and needs no prompt. A held pin whose key was never recorded
    /// cannot prove a renewal, so it counts as a change; an entry with no pin has nothing to replace.
    /// </summary>
    private bool ReplacesHeldPin(string host, int port, string leaf, string? leafKey) =>
        IsAccountEndpoint(host, port) && Account.Pin is { Length: > 0 } held
        && !string.Equals(held, leaf, StringComparison.Ordinal)
        && !(leafKey is not null && string.Equals(Account.PinKey, leafKey, StringComparison.Ordinal));

    /// <summary>The guest entries held beside the own entry (SP-0221 requirement 9); never connected here.</summary>
    internal ExchangeGuestEntries Guests { get; }

    /// <summary>
    /// The one sign-in (DEVICE-EXCHANGE items AA, AD, AO). The own entry is a <c>member</c> entry - the only one
    /// that broadcasts (item AC) - and an account of kind <c>guest</c> goes to <see cref="Guests"/> instead, so a
    /// guest never takes the own slot and never makes this PC leave it. The kind is only known once the server
    /// answers, so when the own slot holds another account the new sign-in is enrolled first as a candidate:
    /// a member result then leaves the old entry in item AB's order and takes its place, a guest result is added
    /// beside it, and a refusal changes nothing.
    /// </summary>
    internal async Task EnrollAsync(string host, int port, string login, bool pairingCode, char[] secret, string? acceptedPin,
        ExchangeAccount? accountToLeave = null, CancellationToken cancellationToken = default,
        bool requireImportedPin = false, bool pinReplacementConfirmed = false)
    {
        try
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Array.Clear(secret);
            ReportOutcome("ExchangeJoinCancelled");
            return;
        }
        ExchangeTlsConnection? connection = null;
        JsonDocument? response = null;
        var savingAccount = false;
        var resumePrevious = false;
        var candidate = false;
        ExchangeE2eKeyPair? deviceKey = null;
        char[]? retrySecret = null;
        try
        {
            EnrollmentOutcomeKey = null;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStorage();
            host = ExchangeAccountIdentity.NormalizeHost(host);
            LastAttemptEndpoint = (host, port);
            login = login.ToLowerInvariant();
            if (login.Length == 0 || secret.Length == 0)
            {
                // The table's own row for an empty credential (item BR: it is refused before the wire).
                ReportOutcome(ExchangeFailureRows.EmptyCredential.MessageKey);
                return;
            }

            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535
                || login.Length is < 3 or > 64 || login.Any(character =>
                    character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '_' and not '-')
                || secret.Length == 0 || (requireImportedPin && acceptedPin is null))
            {
                // Nothing was stopped, so the receiver's connection line is not this attempt's to overwrite.
                ReportOutcome("ExchangeInvalidEntry");
                return;
            }

            var sameOwn = HasAccount && ExchangeAccountIdentity.SameAccount(Account.Host, Account.Port, Account.Login, host, port, login);
            candidate = HasAccount && !sameOwn;
            // The confirmation names the entry the person agreed to leave; it still has to be the current one.
            // Identity, not the whole record: a leaf recorded or a token rotated since the prompt changes bytes,
            // not which account it is.
            if (candidate && (accountToLeave is null || accountToLeave.DeviceId != Account.DeviceId
                || !ExchangeAccountIdentity.SameAccount(accountToLeave.Host, accountToLeave.Port, accountToLeave.Login,
                    Account.Host, Account.Port, Account.Login)))
            {
                ReportOutcome("ExchangeReplacementRequired");
                return;
            }

            // One device id per entry, kept for the entry's life (item AA): the own entry's on a re-enrollment, a
            // guest entry's when this identity is already one, otherwise a fresh one. An empty own slot already
            // holds a fresh id that nothing has used.
            var guestEntry = Guests.Find(host, port, login);
            var deviceId = sameOwn ? Account.DeviceId
                : guestEntry?.DeviceId ?? (HasAccount ? ExchangeProtocol.NewDeviceId() : Account.DeviceId);
            // SP-0226 / item DF: the key of the entry whose device id this enrollment continues. An id the server
            // already knows keeps the key it was enrolled with (a token rotation) - and one enrolled with none sends
            // none, because a known id cannot be given a key (id-collision). A new id gets a new pair, made before
            // `enroll`, and a key lives exactly as long as its id.
            var continuing = sameOwn ? Account : guestEntry;
            var knownId = continuing is not null && continuing.DeviceId == deviceId && continuing.Host.Length > 0;
            deviceKey = knownId ? continuing!.LoadE2eKey() : ExchangeE2eKeyPair.Generate();
            GuestOutcomeKey = null;
            LeaveOutcomeKey = null;
            if (!candidate)
            {
                // The running receiver is stopped so the new enrollment owns the one control connection; any
                // exit that does not replace the account puts it back (the finally below). A candidate for
                // another account touches neither the receiver nor its connection line.
                resumePrevious = _connectionCancellation is not null && Account.Enabled && Account.Token is not null;
                await StopLoopAsync().ConfigureAwait(false);
                SetStatus("ExchangeConnecting");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var onAccountEndpoint = IsAccountEndpoint(host, port);
            var pin = acceptedPin ?? (onAccountEndpoint ? Account.Pin : null);
            var pinKey = onAccountEndpoint ? Account.PinKey : null;
            // A fresh device id (no token yet, so no server has accepted it) that the server reports as
            // id-collision is regenerated once and the join retried on a new stream; the kept id of an
            // enrolled entry is never replaced here, so its collision is shown as the refusal it is.
            var idIsKept = guestEntry is not null || (sameOwn && Account.Token is not null);
            var regenerated = false;
            // The writer wipes the array it is given as soon as the frame is out, so the retry needs an array of its
            // own, made before the first write; an id that is kept never retries and needs none.
            retrySecret = idIsKept ? null : (char[])secret.Clone();
            while (true)
            {
                connection = await ExchangeTlsConnection.OpenAsync(host, port, pin, deadline.Token, pinKey).ConfigureAwait(false);
                if (requireImportedPin && !pinReplacementConfirmed
                    && ReplacesHeldPin(host, port, connection.Fingerprint, connection.PublicKeyFingerprint))
                {
                    // Before the password leaves: the string's leaf is not the entry's server unless the person says so.
                    throw new ExchangeCertificateException(Account.Pin, connection.Fingerprint);
                }

                await ExchangeProtocol.WriteEnrollmentAsync(connection.Stream, ExchangeEnrollmentFields.Build(
                    login, deviceId, "STREAMS Player", "streamsplayer-windows", ProductInfo.Version, "windows", Roles(),
                    ExchangeCapabilities.Receiver, 30, deviceKey),
                    pairingCode ? "pairingCode" : "password", regenerated ? retrySecret! : secret, deadline.Token).ConfigureAwait(false);
                response = await ExchangeHandshake.ReadAsync(connection.Stream, "enrolled", deadline.Token).ConfigureAwait(false);
                var root = response.RootElement;
                if (ExchangeProtocol.String(root, "type") != "refused")
                {
                    break;
                }

                var reason = ExchangeProtocol.String(root, "reason");
                response.Dispose();
                response = null;
                connection.Dispose();
                connection = null;
                if (reason != "id-collision" || idIsKept || regenerated)
                {
                    RefuseEnrollment(candidate, ExchangeProtocol.RefusalKey(reason, oneTimeCode: pairingCode));
                    return;
                }

                regenerated = true;
                deviceId = ExchangeProtocol.NewDeviceId();
                deviceKey?.Dispose();
                deviceKey = ExchangeE2eKeyPair.Generate();
                if (!candidate)
                {
                    Account = Account with { DeviceId = deviceId };
                }
            }

            var accepted = response!.RootElement;
            var token = ExchangeProtocol.String(accepted, "deviceToken");
            if (token is null || token.Length != 43 || token.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new InvalidDataException("Invalid device token.");
            }

            var interval = ExchangeHandshake.ReadInterval(accepted);
            var kind = ExchangeAccountKinds.Parse(ExchangeProtocol.String(accepted, "accountKind"));
            cancellationToken.ThrowIfCancellationRequested();

            if (kind == ExchangeAccountKind.Guest)
            {
                savingAccount = true;
                await KeepGuestEntryAsync(connection, new ExchangeAccount(deviceId, host, port, login, token,
                    connection.Fingerprint, true, false, connection.PublicKeyFingerprint, Guest: true).WithE2eKey(deviceKey), sameOwn)
                    .ConfigureAwait(false);
                savingAccount = false;
                // The own entry, when it was left running, is as it was; a demoted one is gone from its slot
                // and the guard below finds no token to resume with.
                return;
            }

            if (candidate)
            {
                // The new entry is signed in and is a member's: only now does the old one go, in item AB's order.
                await LeaveOwnEntryAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            RememberServerFacts(accepted);
            cancellationToken.ThrowIfCancellationRequested();
            var enrolled = new ExchangeAccount(deviceId, host, port, login, token, connection.Fingerprint, true,
                Account.AutoAcceptCasts, connection.PublicKeyFingerprint).WithE2eKey(deviceKey);
            savingAccount = true;
            _store.Save(enrolled);
            if (guestEntry is not null)
            {
                // A promoted account: the same device continues as the own entry, so nothing is revoked.
                Guests.Remove(host, port, login);
            }

            savingAccount = false;
            Account = enrolled;
            ClearPendingCertificate();
            SignedOutNotice = false;
            resumePrevious = false;
            StartLoop(connection, interval);
            connection = null;
        }
        catch (ExchangeCertificateException exception)
        {
            if (requireImportedPin)
            {
                RefuseEnrollment(candidate, "ExchangeStringCertificateMismatch");
                throw;
            }

            EnrollmentOutcomeKey = ExchangeFailures.CertificateKey(exception);
            if (candidate)
            {
                // The certificate to confirm belongs to another server than the receiver's own connection line.
                Changed?.Invoke();
            }
            else
            {
                ShowCertificate(exception, host, port);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RefuseEnrollment(candidate, "ExchangeJoinCancelled");
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            RefuseEnrollment(candidate, savingAccount || _storageUnavailable || exception is UnauthorizedAccessException
                ? "ExchangeStorageFailed" : ExchangeFailures.KeyOf(exception, connectionOpen: connection is not null));
        }
        finally
        {
            Array.Clear(secret);
            if (retrySecret is not null)
            {
                Array.Clear(retrySecret);
            }

            deviceKey?.Dispose();
            connection?.Dispose();
            response?.Dispose();
            // resumePrevious was read before the receiver was stopped. A loop that was still running then may
            // have handled a revocation or a bad-credentials refusal while it wound down, which clears the
            // token and the enabled flag: starting it again would reconnect a token-less account.
            if (resumePrevious && _connectionCancellation is null && Account.Enabled && Account.Token is not null)
            {
                // The outcome of this attempt is kept in EnrollmentOutcomeKey; the receiver's own state
                // follows from here on, without announcing "connecting" over the refusal first.
                StartLoop(null, 0, announceConnecting: false);
            }

            _operations.Release();
        }
    }

    /// <summary>
    /// A refusal or failure of one enrollment. A candidate for another account never stopped the receiver, so
    /// its connection line is not touched and only the outcome line says what happened; an enrollment that did
    /// stop the receiver shows the failure on the connection line until the receiver is back.
    /// </summary>
    private void RefuseEnrollment(bool candidate, string key)
    {
        if (candidate)
        {
            ReportOutcome(key);
        }
        else
        {
            FailEnrollment(key);
        }
    }

    /// <summary>
    /// Keeps a guest account's sign-in (items AO and AA): a stored entry that nothing connects here and that sends
    /// nothing, so it announces nothing on the server. The enrollment's connection is closed politely, and a device
    /// the server now holds that this PC cannot keep is revoked on it rather than left as a ghost.
    /// </summary>
    private async Task KeepGuestEntryAsync(ExchangeTlsConnection connection, ExchangeAccount entry, bool ownEntryDemoted)
    {
        if (Guests.Find(entry.Host, entry.Port, entry.Login) is null && Guests.IsFull)
        {
            if (ownEntryDemoted)
            {
                // The account is a guest's now and there is no room beside the own entry, so it stays where it
                // is, flagged: signed in with its new token, producing nothing.
                var kept = Account with
                {
                    Token = entry.Token, Pin = entry.Pin, PinKey = entry.PinKey, Guest = true, SignedOut = false
                };
                _store.Save(kept);
                Account = kept;
            }
            else
            {
                // A device the server now holds that this PC cannot keep is revoked rather than left a ghost.
                await TryRevokeAsync(connection, entry.DeviceId).ConfigureAwait(false);
            }

            ReportOutcome("ExchangeGuestLimit");
            return;
        }

        if (ownEntryDemoted)
        {
            if (BeforeLeaveAsync is { } endBroadcasts)
            {
                await endBroadcasts().ConfigureAwait(false);
            }

            ClearDirectory();
        }

        Guests.Upsert(entry);
        if (!HasAccount || ownEntryDemoted)
        {
            // This device id now belongs to the guest entry; the own slot gets its own (item AA: one id per entry).
            ClearPendingCertificate();
            Account = new ExchangeAccount(ExchangeProtocol.NewDeviceId());
            _store.Save(Account);
        }

        await TryByeAsync(connection).ConfigureAwait(false);
        EnrollmentOutcomeKey = null;
        GuestOutcomeKey = "ExchangeGuestSignedIn";
        Changed?.Invoke();
    }

    /// <summary>The last outcome of a guest entry operation, shown beside the list; null when nothing happened.</summary>
    internal string? GuestOutcomeKey { get; private set; }

    /// <summary>
    /// The new member's sign-in has succeeded, so the old own entry goes. An entry that was only ever flagged
    /// as a guest's (its account was demoted while signed in) is not a device to remove from the account: it
    /// moves beside the new entry, same device, same token, and nothing is revoked.
    /// </summary>
    private async Task LeaveOwnEntryAsync()
    {
        if (Account.Guest && Account.Token is not null && (!Guests.IsFull || Guests.Find(Account.Host, Account.Port, Account.Login) is not null))
        {
            if (BeforeLeaveAsync is { } endBroadcasts)
            {
                await endBroadcasts().ConfigureAwait(false);
            }

            await StopLoopAsync().ConfigureAwait(false);
            Guests.Upsert(Account with { Enabled = true, Guest = true });
            ClearPendingCertificate();
            ClearDirectory();
            Account = new ExchangeAccount(ExchangeProtocol.NewDeviceId());
            _store.Save(Account);
            return;
        }

        await LeaveAccountAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Signs a guest entry out on this device (items AV and AB, step for step): one bounded <c>hello</c> and the
    /// device's own <c>revoke</c>, then forgotten locally. Never depends on the server, never retried.
    /// </summary>
    internal async Task SignOutGuestAsync(ExchangeAccount entry, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStorage();
            if (Guests.Find(entry.Host, entry.Port, entry.Login) is not { } held)
            {
                return;
            }

            var told = held.Token is not null && await RevokeOfflineAccountAsync(held).ConfigureAwait(false);
            Guests.Remove(held.Host, held.Port, held.Login);
            GuestOutcomeKey = told ? "ExchangeGuestSignedOut" : "ExchangeGuestSignedOutOffline";
            Changed?.Invoke();
        }
        finally
        {
            _operations.Release();
        }
    }
}
