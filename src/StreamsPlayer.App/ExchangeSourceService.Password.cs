using System.IO;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>What <see cref="ExchangeSourceService.ChangePasswordAsync"/> ended with: the words to show and whether it worked.</summary>
internal sealed record ExchangePasswordChangeResult(bool Changed, string Key);

/// <summary>
/// SP-0221: the account's own changes of state on a live session - a password changed here (item AT), an account
/// that ended this device's sign-in, a leaf renewed with the same key (item AY) and an account that turned out to
/// be a guest's (item AO). None of it opens a connection of its own: every one is read off the control stream the
/// receiver already holds.
/// </summary>
internal sealed partial class ExchangeSourceService
{
    // One password change at a time (requirement 12): the request is written once, and this stays set until the
    // server answers or the session ends - even after the caller stopped waiting - so a late answer, which may be
    // the only copy of this device's new token, is still read as the answer and never mistaken for a revocation.
    private TaskCompletionSource<ExchangePasswordChangeResult>? _pendingPasswordChange;

    /// <summary>
    /// True from the moment a live session found this device signed out by its account until the user has been
    /// told. The main window shows the message and the prompt once, then acknowledges.
    /// </summary>
    internal bool SignedOutNotice { get; private set; }

    internal void AcknowledgeSignedOutNotice() => SignedOutNotice = false;

    /// <summary>False for an own entry whose account is a guest's: the server would refuse every producer operation.</summary>
    internal bool MayBroadcast => !Account.Guest;

    /// <summary>
    /// Changes the password of the signed-in account (item AT): <c>password-change</c> on the live control stream,
    /// one request and one answer. A wrong old password or a password outside the policy is one refusal, the
    /// session stays open and nothing changes. On success the answer carries this device's new token, which is
    /// stored before anything else happens. Never resent: the server checks one password a second (item AW).
    /// </summary>
    internal async Task<ExchangePasswordChangeResult> ChangePasswordAsync(char[] oldPassword, char[] newPassword,
        CancellationToken cancellationToken = default)
    {
        var change = new ExchangePasswordChange(oldPassword, newPassword);
        try
        {
            if (oldPassword.Length == 0 || newPassword.Length == 0)
            {
                return new ExchangePasswordChangeResult(false, "ExchangePasswordEmpty");
            }

            if (Account.Token is null)
            {
                return new ExchangePasswordChangeResult(false, "ExchangePasswordNotSignedIn");
            }

            var waiter = new TaskCompletionSource<ExchangePasswordChangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _pendingPasswordChange, waiter, null) is not null)
            {
                return new ExchangePasswordChangeResult(false, "ExchangePasswordBusy");
            }

            bool written;
            try
            {
                written = await PostControlEnvelopeAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.CompareExchange(ref _pendingPasswordChange, null, waiter);
                throw;
            }

            if (!written)
            {
                // Nothing reached the server: no answer is owed, so the single flight is released.
                Interlocked.CompareExchange(ref _pendingPasswordChange, null, waiter);
                return new ExchangePasswordChangeResult(false, ExchangeFailureRows.Unreachable.MessageKey);
            }

            try
            {
                return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The person stopped waiting; the request is out and the answer, if it comes, is still read.
                return new ExchangePasswordChangeResult(false, ExchangeFailureRows.Unanswered.MessageKey);
            }
        }
        finally
        {
            change.Clear();
        }
    }

    /// <summary>The server's answer to a <c>password-change</c>, read in the control loop: the fresh token is kept at once.</summary>
    private void HandlePasswordChanged(JsonElement root)
    {
        var pending = _pendingPasswordChange;
        if (pending is null)
        {
            // Nothing was asked: an unsolicited answer changes no credential.
            return;
        }

        var token = ExchangeProtocol.String(root, "deviceToken");
        if (token is null || token.Length != 43 || token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("Invalid device token.");
        }

        var updated = Account with { Token = token, SignedOut = false };
        try
        {
            _store.Save(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            // The old token is dead on the server; keeping the new one in memory is all that is left, and the
            // storage failure is the message. The session itself is healthy.
            _storageUnavailable = true;
            Account = updated;
            Interlocked.CompareExchange(ref _pendingPasswordChange, null, pending);
            pending.TrySetResult(new ExchangePasswordChangeResult(true, "ExchangeStorageFailed"));
            return;
        }

        Account = updated;
        Interlocked.CompareExchange(ref _pendingPasswordChange, null, pending);
        pending.TrySetResult(new ExchangePasswordChangeResult(true, "ExchangePasswordChanged"));
        Changed?.Invoke();
    }

    /// <summary>
    /// A <c>refused</c> that arrives while a password change is pending is that change's answer: the reasons a
    /// password check has (items AT, AW) - and never a revocation of this device, which is how the same words read
    /// when they open a session. Returns true when it was consumed.
    /// </summary>
    private bool TryAnswerPasswordChange(string? reason)
    {
        var pending = _pendingPasswordChange;
        if (pending is null)
        {
            return false;
        }

        // The old password being wrong is this operation's own wording ("The old password is wrong."); every other
        // answer is the table's row for the reason, so the same refusal reads the same everywhere (item DA).
        var key = reason switch
        {
            "bad-credentials" => "ExchangePasswordOldWrong",
            "password-policy" or "rate-limited" or "limit-reached" or "not-permitted" or "unavailable" or "capacity"
                => ExchangeFailureRows.OfWire(reason).MessageKey,
            _ => null
        };
        if (key is null)
        {
            return false;
        }

        Interlocked.CompareExchange(ref _pendingPasswordChange, null, pending);
        pending.TrySetResult(new ExchangePasswordChangeResult(false, key));
        return true;
    }

    /// <summary>The session ended with a password change unanswered: whoever waits is told, and the flight is over.</summary>
    private void AbandonPasswordChange()
    {
        var pending = Interlocked.Exchange(ref _pendingPasswordChange, null);
        pending?.TrySetResult(new ExchangePasswordChangeResult(false, ExchangeFailureRows.Unanswered.MessageKey));
    }

    /// <summary>
    /// Item AY, after a connection that was accepted: the leaf's fingerprint and public key are recorded as the
    /// entry's pin. A renewal with the same key replaces the kept leaf (no prompt); an entry that kept only a
    /// leaf gains its key here, so the first renewal after this release is already told from another server.
    /// </summary>
    private void RememberLeaf(ExchangeTlsConnection connection)
    {
        if (Account.Token is null || connection.Fingerprint.Length == 0 || connection.PublicKeyFingerprint is null
            || (Account.Pin == connection.Fingerprint && Account.PinKey == connection.PublicKeyFingerprint))
        {
            return;
        }

        var updated = Account with { Pin = connection.Fingerprint, PinKey = connection.PublicKeyFingerprint };
        try
        {
            _store.Save(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            // The connection is accepted either way; the old pin still names this server's key.
            return;
        }

        Account = updated;
    }

    /// <summary>
    /// The server's welcome names this own entry's account a member's after it was flagged as a guest's (item AO: the
    /// operator may change the kind either way). The flag came from the same server and goes with its next word, so
    /// the producer actions come back at once and not at the next sign-in.
    /// </summary>
    private void AccountBecameMember()
    {
        if (!Account.Guest)
        {
            return;
        }

        var restored = Account with { Guest = false };
        Account = restored;
        if (GuestOutcomeKey == "ExchangeGuestLimit")
        {
            GuestOutcomeKey = null;
        }

        try
        {
            _store.Save(restored);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            _storageUnavailable = true;
        }
    }

    /// <summary>
    /// The server's welcome says this own entry's account is a guest's (item AO; an operator changed its kind).
    /// The entry moves beside the own slot - same device, same token, nothing revoked - and the receiver ends,
    /// because a guest's session is not this slot's. With no room beside it the entry stays, flagged, and the
    /// session goes on without a producer. Returns true when the entry left the slot.
    /// </summary>
    private async Task<bool> AccountBecameGuestAsync()
    {
        if (Account.Guest || Account.Token is null)
        {
            // Already flagged (it had no room before): the session simply goes on without a producer.
            return false;
        }

        var moved = Account with { Guest = true, Enabled = true };
        if (Guests.Find(moved.Host, moved.Port, moved.Login) is null && Guests.IsFull)
        {
            Account = moved;
            try
            {
                _store.Save(moved);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
            {
                _storageUnavailable = true;
            }

            GuestOutcomeKey = "ExchangeGuestLimit";
            return false;
        }

        if (BeforeLeaveAsync is { } endBroadcasts)
        {
            await endBroadcasts().ConfigureAwait(false);
        }

        try
        {
            Guests.Upsert(moved);
            Account = new ExchangeAccount(ExchangeProtocol.NewDeviceId());
            _store.Save(Account);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            _storageUnavailable = true;
            SetStatus("ExchangeStorageFailed");
            return true;
        }

        ClearPendingCertificate();
        ClearDirectory();
        GuestOutcomeKey = "ExchangeGuestMoved";
        SetStatus("ExchangeOff");
        return true;
    }
}
