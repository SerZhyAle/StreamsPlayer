using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class DriveSourceService : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly DriveAccountStore _store;
    private readonly DriveOAuth? _oauth;
    private readonly string _clientHash;
    private readonly DriveDirectory _directory;
    private DriveAccount? _account;
    private DrivePublisher? _publisher;
    private DriveTokens? _tokens;
    private long _refreshAt;
    private readonly Func<DriveOAuth, CancellationToken, Task<DriveTokens>> _authorize;

    internal DriveSourceService(string directory, HttpMessageHandler? handler = null, string? clientIdOverride = null,
        string? clientSecretOverride = null, Func<DriveOAuth, CancellationToken, Task<DriveTokens>>? authorize = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _authorize = authorize ?? SignInBrowserAsync;
        _store = new DriveAccountStore(directory);
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Setting(string key) => metadata.FirstOrDefault(item => item.Key == key)?.Value
            is { Length: > 0 } value ? value : Environment.GetEnvironmentVariable(key);
        var clientId = clientIdOverride ?? Setting("SP_DRIVE_CLIENT_ID");
        _clientHash = clientId is null ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)));
        _oauth = clientId is null ? null : new DriveOAuth(_http, clientId, clientSecretOverride ?? Setting("SP_DRIVE_CLIENT_SECRET"));
        Client = new DriveRestClient(_http, AccessTokenAsync);
        _directory = new DriveDirectory(Client);
        if (_oauth is not null)
        {
            try { _account = _store.Load(_clientHash); }
            // S2-1: the store throws InvalidDataException for an oversize file and a locked or foreign-ACL file
            // raises UnauthorizedAccessException; either escaping this constructor ends the process at every launch.
            catch (Exception exception) when (exception is IOException or CryptographicException or JsonException
                or InvalidDataException or UnauthorizedAccessException)
            { StatusKey = "DriveStorageFailed"; }
        }
        ResetPublisher();
        if (_account is not null)
            StatusKey = "DriveConnected";
        _publicationTask = RunPublicationAsync();
    }

    internal DriveRestClient Client { get; }
    internal bool Available => _oauth is not null;
    internal bool SignedIn => _account is not null;
    internal string StatusKey { get; private set; } = "DriveSignedOut";
    internal string EntryIdentity => "drive:" + (_account?.DeviceId ?? "");
    internal string? DeviceId => _account?.DeviceId;
    internal ExchangeDirectorySnapshot? Directory { get; private set; }
    internal event Action? Changed;

    private async Task<string> AccessTokenAsync(CancellationToken cancellation)
    {
        var account = _account ?? throw new InvalidOperationException("Google sign-in is required.");
        if (_tokens is null || Stopwatch.GetTimestamp() >= _refreshAt)
        {
            var tokens = await _oauth!.RefreshAsync(account.RefreshToken, cancellation).ConfigureAwait(false);
            if (tokens.RefreshToken is { Length: > 0 } refresh)
            {
                _account = account with { RefreshToken = refresh };
                await _store.SaveAsync(_account, cancellation).ConfigureAwait(false);
            }
            AcceptTokens(tokens);
        }
        return _tokens!.AccessToken;
    }

    private void AcceptTokens(DriveTokens tokens)
    {
        _tokens = tokens;
        _refreshAt = Stopwatch.GetTimestamp() + (long)(Math.Max(1, tokens.ExpiresIn - 60) * (double)Stopwatch.Frequency);
    }

    private void ResetPublisher() => _publisher = _account is null ? null :
        new DrivePublisher(Client, _account.OwnedFiles, cancellation => _store.SaveAsync(_account!, cancellation));

    internal async Task SignInAsync(CancellationToken cancellation)
    {
        if (_oauth is null)
            return;
        await _operations.WaitAsync(cancellation);
        try
        {
            if (_account is not null)
                return;
            StatusKey = "DriveSigningIn";
            Changed?.Invoke();
            var tokens = await _authorize(_oauth, cancellation);
            if (string.IsNullOrEmpty(tokens.RefreshToken))
                throw new InvalidDataException("Google returned no refresh token.");
            var account = new DriveAccount(ExchangeProtocol.NewDeviceId(), _clientHash, tokens.RefreshToken, []);
            try { await _store.SaveAsync(account, cancellation); }
            catch
            {
                await _oauth.RevokeAsync(tokens.RefreshToken, CancellationToken.None);
                throw;
            }
            _account = account;
            AcceptTokens(tokens);
            ResetPublisher();
            StatusKey = "DriveConnected";
        }
        catch (Exception exception) when (IsFailure(exception)) { StatusKey = "DriveSignInFailed"; }
        finally { _operations.Release(); Changed?.Invoke(); }
        if (SignedIn)
            await PublishCurrentAsync(cancellation);
    }

    internal async Task SignOutAsync(CancellationToken cancellation)
    {
        await _operations.WaitAsync(cancellation);
        var revoked = true;
        try
        {
            if (_account is not { } account)
                return;
            try
            {
                if (_publisher is not null)
                {
                    using var deadline = DeadlineFor(cancellation);
                    await _publisher.SynchronizeAsync(account.DeviceId, [], DateTimeOffset.UtcNow, TimeSpan.Zero, deadline.Token);
                }
            }
            catch (Exception exception) when (IsFailure(exception)) { revoked = false; }
            try
            {
                using var deadline = DeadlineFor(cancellation);
                await _oauth!.RevokeAsync(account.RefreshToken, deadline.Token);
            }
            catch (Exception exception) when (IsFailure(exception)) { revoked = false; }
            _store.Forget();
            _account = null;
            _tokens = null;
            Directory = null;
            _directory.Clear();
            ResetPublisher();
            StatusKey = revoked ? "DriveSignedOut" : "DriveRevocationFailed";
        }
        finally { _operations.Release(); Changed?.Invoke(); }
    }

    internal async Task<bool> PollAsync(CancellationToken cancellation)
    {
        await _operations.WaitAsync(cancellation);
        var previousDirectory = Directory;
        var previousStatus = StatusKey;
        try
        {
            if (_account is null)
                return false;
            using var deadline = DeadlineFor(cancellation);
            Directory = await _directory.PollAsync(_account.DeviceId, deadline.Token);
            StatusKey = _publicationFailed ? "DrivePublishFailed" : "DriveConnected";
            return true;
        }
        catch (Exception exception) when (IsFailure(exception) && !cancellation.IsCancellationRequested)
        {
            if (exception is DriveRequestException { StatusCode: 400 or 401 })
            {
                ForgetFailedGrant();
            }
            else StatusKey = "DriveReadFailed";
            return false;
        }
        finally
        {
            _operations.Release();
            if (!ReferenceEquals(previousDirectory, Directory) || previousStatus != StatusKey)
                Changed?.Invoke();
        }
    }

    // S2-2: InvalidDataException is the failure type this service and Core's DriveOAuth/DriveRestClient throw for a
    // refused or malformed answer; ArgumentException (an invalid outgoing record) and UnauthorizedAccessException
    // (the protected store) are failures of the same kind. Each handler then sets its own failure status.
    internal static bool IsFailure(Exception exception) => exception is IOException or HttpRequestException
        or OperationCanceledException or JsonException or CryptographicException or InvalidOperationException
        or System.ComponentModel.Win32Exception or InvalidDataException or ArgumentException
        or UnauthorizedAccessException;

    // S2-4: HttpClient.Timeout ends at the response headers, while body reads and the token refresh run under the
    // caller's token. Every call that holds _operations therefore gets its own deadline; its expiry cancels only
    // this linked token, so the handlers' `!cancellation.IsCancellationRequested` filters read it as a failure
    // (DrivePublishFailed / DriveReadFailed) and not as a user cancellation.
    private static readonly TimeSpan OperationDeadline = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource DeadlineFor(CancellationToken cancellation)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(OperationDeadline);
        return linked;
    }

    private void ForgetFailedGrant()
    {
        _store.Forget();
        _account = null;
        _tokens = null;
        Directory = null;
        _directory.Clear();
        ResetPublisher();
        StatusKey = "DriveSignInFailed";
    }

    public void Dispose() { _lifetime.Cancel(); _http.Dispose(); }
}
