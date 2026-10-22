using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class ExchangeSourceService
{
    private ExchangeAccount? _accountBeingRemoved;
    private bool _removalWritten;
    internal string? LeaveOutcomeKey { get; private set; }

    /// <summary>
    /// SP-0217 constraint 6: a leave ends the running broadcasts first, while the control stream is
    /// still alive to carry the `broadcast-end` - the order the acceptance criteria read back is
    /// `broadcast-end` before `bye` before `revoke`, and this hook is the one place that guarantees it.
    /// </summary>
    internal Func<Task>? BeforeLeaveAsync;

    internal bool HasAccount => Account.Host.Length > 0;
    internal bool RequiresLeave(string host, int port, string login) => HasAccount
        && !ExchangeAccountIdentity.SameAccount(Account.Host, Account.Port, Account.Login, host, port, login);

    // The operations gate is held by Forget or Enroll. The loop first cancels and observes its readers
    // and cast writes, then its finally can revoke on the same authenticated control stream safely.
    private async Task LeaveAccountAsync()
    {
        if (!HasAccount)
        {
            return;
        }

        var leaving = Account;
        _accountBeingRemoved = leaving;
        _removalWritten = false;
        try
        {
            if (BeforeLeaveAsync is { } endBroadcasts)
            {
                await endBroadcasts().ConfigureAwait(false);
            }

            await StopLoopAsync().ConfigureAwait(false);
            if (!_removalWritten && leaving.Token is not null)
            {
                _removalWritten = await RevokeOfflineAccountAsync(leaving).ConfigureAwait(false);
            }

            var forgotten = new ExchangeAccount(ExchangeProtocol.NewDeviceId());
            // Never let a storage error resume an identity already revoked on the server.
            Account = forgotten;
            // SP-0226 / item DG: the entry is gone, so the keys it had seen go with it (and its own key with the record).
            _trustBook.Forget(leaving.DeviceId);
            ClearPendingCertificate();
            ClearDirectory();
            try
            {
                _store.Save(forgotten);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
            {
                _storageUnavailable = true;
                SetStatus("ExchangeStorageFailed");
                throw;
            }

            LeaveOutcomeKey = _removalWritten ? "ExchangeForgotten" : "ExchangeForgottenOffline";
            SetStatus(LeaveOutcomeKey);
        }
        finally
        {
            _accountBeingRemoved = null;
        }
    }

    private static async Task<bool> RevokeOfflineAccountAsync(ExchangeAccount account)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var connection = await ExchangeTlsConnection.OpenAsync(account.Host, account.Port, account.Pin,
                deadline.Token, account.PinKey).ConfigureAwait(false);
            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "hello", deviceId = account.DeviceId,
                deviceToken = account.Token, productVersion = ProductInfo.Version, keepaliveSeconds = 30
            }, false, deadline.Token).ConfigureAwait(false);
            using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", deadline.Token).ConfigureAwait(false);
            if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
            {
                return false;
            }

            _ = ExchangeHandshake.ReadInterval(welcome.RootElement);
            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "revoke", deviceId = account.DeviceId
            }, true, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // Removal has no background work: the local account is forgotten even without a server.
            return false;
        }
    }

    private static async Task<bool> TryRevokeAsync(ExchangeTlsConnection connection, string deviceId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "revoke", deviceId
            }, true, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            return false;
        }
    }
}
