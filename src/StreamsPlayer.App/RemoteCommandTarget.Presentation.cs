using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal enum RemoteConnectionState
{
    Off,
    NoServer,
    SignInAgain,
    Offline,
    Ready
}

internal sealed partial class RemoteCommandTarget
{
    private readonly object _directoryGate = new();
    private IDisposable? _directoryLease;
    private bool _stopping;
    private volatile IReadOnlyList<ExchangeDeviceRecord> _controllers = [];
    private volatile RemoteConsentQuestion? _pending;

    internal event Action? PresentationChanged;
    internal event Action? ConsentChanged;
    internal IReadOnlyList<ExchangeDeviceRecord> Controllers => _controllers;
    internal RemoteConsentQuestion? PendingConsent => _pending;

    internal RemoteConnectionState ConnectionState
    {
        get
        {
            if (!Settings.Listening)
            {
                return RemoteConnectionState.Off;
            }

            if (!_exchange.IsActive || _exchange.Account.Guest || IsOnline && !CarriesCommands)
            {
                return RemoteConnectionState.NoServer;
            }

            if (!_exchange.Account.DeclaresCommands)
            {
                return RemoteConnectionState.SignInAgain;
            }

            return IsOnline ? RemoteConnectionState.Ready : RemoteConnectionState.Offline;
        }
    }

    private bool IsOnline => _exchange.StatusKey == "ExchangeOnline";
    private bool CarriesCommands => _exchange.Features.Contains("command", StringComparer.Ordinal);

    private void OnExchangeChanged()
    {
        lock (_directoryGate)
        {
            // Follow while listening, even with the window closed, so a withdrawn controller loses access promptly.
            if (!_stopping && Settings.Listening && _exchange.IsActive && !_exchange.Account.Guest)
            {
                _directoryLease ??= _exchange.AcquireDirectory(follow: true);
            }
            else
            {
                _directoryLease?.Dispose();
                _directoryLease = null;
            }
        }

        PresentationChanged?.Invoke();
    }

    private void StopFollowing()
    {
        _exchange.Changed -= OnExchangeChanged;
        _exchange.DirectoryChanged -= OnDirectoryChanged;
        lock (_directoryGate)
        {
            _stopping = true;
            _directoryLease?.Dispose();
            _directoryLease = null;
        }
    }

    private void OnDirectoryChanged()
    {
        var devices = _exchange.IsDirectoryFresh ? _exchange.Directory?.Devices : null;
        var accountDeviceId = _exchange.Account.DeviceId;
        var eligible = devices?.Where(device => device.DeviceId != accountDeviceId &&
            _exchange.StateOfDevice(device) == ExchangeSealedState.None).ToArray() ?? [];
        _controllers = eligible;
        if (devices is not null)
        {
            var allowed = eligible.Select(device => device.DeviceId).ToHashSet(StringComparer.Ordinal);
            var revoked = Settings.Grants.Where(grant => !allowed.Contains(grant.DeviceId))
                .Select(grant => grant.DeviceId).ToHashSet(StringComparer.Ordinal);
            if (revoked.Count > 0)
            {
                _ = UpdateSettingsAsync(settings =>
                {
                    if (_exchange.Account.DeviceId != accountDeviceId)
                    {
                        return settings;
                    }

                    var kept = settings.Grants.Where(grant => !revoked.Contains(grant.DeviceId)).ToArray();
                    return kept.Length == settings.Grants.Count ? settings : settings with { Grants = kept };
                }, "controller-no-longer-eligible");
            }
        }

        PresentationChanged?.Invoke();
    }

    internal bool IsController(string deviceId) => _exchange.IsActive && _exchange.IsDirectoryFresh &&
        Controllers.Any(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));

    private async Task<bool> AskAsync(RemoteConsentQuestion question, CancellationToken cancellationToken)
    {
        if (ShowConsent is not { } show)
        {
            return false;
        }

        _pending = question;
        ConsentChanged?.Invoke();
        try
        {
            show(question);
            return await question.Answer.WaitAsync(ConsentWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            question.Finish(false);
            _pending = null;
            ConsentChanged?.Invoke();
        }
    }
}

internal sealed class RemoteConsentQuestion(
    string deviceName, AppCommandEntry command, IReadOnlyList<string> words, DateTimeOffset deadline)
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string DeviceName { get; } = deviceName;
    internal AppCommandEntry Command { get; } = command;
    internal IReadOnlyList<string> Words { get; } = words;
    internal DateTimeOffset Deadline { get; } = deadline;
    internal Task<bool> Answer => _answer.Task;
    internal int SecondsLeft => Math.Max(0, (int)Math.Ceiling((Deadline - DateTimeOffset.UtcNow).TotalSeconds));
    internal void Finish(bool accepted) => _answer.TrySetResult(accepted && DateTimeOffset.UtcNow < Deadline);
}
