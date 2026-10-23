using System.Net.Sockets;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// One connection, shared by the main audio route and broadcast sessions. Recovery belongs to the client;
/// a replacement leg always walks the descriptor from the top and owns no resource of its predecessor.
/// </summary>
internal sealed class FastMediaSorterAudioLeg : IDisposable
{
    private readonly object _gate = new();
    private readonly FastMediaSorterAudioPlayback _playback;
    private readonly Action<string, string[]>? _log;
    private ExchangeTunnelForwarder? _forwarder;
    private ExchangeRelayProxy? _relayProxy;
    private bool _streaming;
    private bool _disposed;

    internal FastMediaSorterAudioLeg(Action<string, string[]>? log)
    {
        _log = log;
        _playback = new FastMediaSorterAudioPlayback(log);
        _playback.Playing += Playback_Playing;
        _playback.Ended += Playback_Ended;
        _playback.Failed += Playback_Failed;
    }

    internal event EventHandler<FastMediaSorterAudioPlaybackEventArgs>? Playing;
    internal event EventHandler<FastMediaSorterAudioPlaybackEventArgs>? Ended;
    internal event EventHandler<FastMediaSorterAudioPlaybackEventArgs>? Failed;
    internal event Action<string>? RelayFailed;

    internal string? AudioOutputDevice
    {
        set
        {
            lock (_gate)
            {
                if (!_disposed) _playback.AudioOutputDevice = value;
            }
        }
    }

    internal AudioChannelMode AudioChannelMode
    {
        set
        {
            lock (_gate)
            {
                if (!_disposed) _playback.AudioChannelMode = value;
            }
        }
    }

    internal void SetVolume(int volume)
    {
        lock (_gate)
        {
            if (!_disposed) _playback.SetVolume(volume);
        }
    }

    internal bool StartRecording(RecordingTarget target, CurrentLog log)
    {
        lock (_gate) return !_disposed && _playback.StartRecording(target, log);
    }

    internal BroadcastAudioRecorder? EndRecording()
    {
        lock (_gate) return _disposed ? null : _playback.EndRecording();
    }

    internal bool IsRecordingWriting
    {
        get { lock (_gate) return !_disposed && _playback.IsRecordingWriting; }
    }

    internal bool CanRecord
    {
        get { lock (_gate) return !_disposed && _playback.CanRecord; }
    }

    internal async Task<FastMediaSorterAudioLegResult> OpenAsync(
        StreamChannel channel, int volume, ExchangeRelayCredentials? credentials,
        CancellationToken cancellationToken, int leg, bool reconnecting)
    {
        // Even a local refusal must arrive after the client has published its session and resume record.
        await Task.Yield();
        var listed = channel.FastMediaSorterBroadcast?.PlaybackAttemptEndpoints(channel.Url);
        var attempts = listed is { Count: > 0 }
            ? listed
            : (IReadOnlyList<FastMediaSorterBroadcastEndpoint>)[new(channel.Url, "HTTP", null, null, null, null, null, null, null)];
        FastMediaSorterAudioOpenResult result = new(null, TimeSpan.Zero, null, Cancelled: false);
        var reason = "unsupported_address";
        var playedAt = -1;
        for (var index = 0; index < attempts.Count; index++)
        {
            if (IsCancelled(cancellationToken)) return Cancelled();
            var endpoint = attempts[index];
            var decision = ExchangeBroadcastKeys.Decide(endpoint);
            if (!decision.Allowed)
            {
                reason = decision.Reason;
                LogAttempt(index, endpoint, reason);
                continue;
            }

            if (channel.FastMediaSorterBroadcast?.Auth is not null && decision.RelayOptions(endpoint.Url) is null)
            {
                reason = ExchangeBroadcastAuth.UnsupportedEndpointReason;
                LogAttempt(index, endpoint, reason);
                continue;
            }

            Uri? address = null;
            try
            {
                // Publish transports under the same lock as disposal: stopping during creation must not leak a slot.
                lock (_gate)
                {
                    if (_disposed || cancellationToken.IsCancellationRequested) return Cancelled();
                    if (endpoint.Inner is { Length: > 0 } inner &&
                        ExchangeTunnelUrl.TryParse(endpoint.Url, out var tunnelUrl))
                    {
                        _forwarder = ExchangeTunnelForwarder.Start(tunnelUrl, inner, endpoint.CertFingerprint, tunnelPin: decision.TunnelPin);
                        LaunchableAddress.TryParseHttp(_forwarder.LoopbackUrl, out address);
                    }
                    else if (decision.RelayOptions(endpoint.Url) is { } sealedRelay)
                    {
                        var proxy = ExchangeRelayProxy.Start(endpoint.Url, endpoint.CertFingerprint,
                            credentials: decision.Credentials ?? credentials, sealedRelay: sealedRelay);
                        _relayProxy = proxy;
                        proxy.Failed += failure => RelayProxyFailed(proxy, failure);
                        LaunchableAddress.TryParseHttp(proxy.LoopbackUrl, out address);
                    }
                    else
                    {
                        LaunchableAddress.TryParseHttp(endpoint.Url, out address);
                    }
                }
            }
            catch (ExchangeBroadcastCapacityException)
            {
                reason = ExchangeBroadcastResources.CapacityReason;
                LogAttempt(index, endpoint, reason);
                break;
            }
            catch (Exception exception) when (exception is SocketException or ArgumentException or InvalidOperationException)
            {
                _log?.Invoke(endpoint.Transport == "TUNNEL" ? "FMS AUDIO TUNNEL ERROR" : "FMS AUDIO RELAY ERROR",
                    [$"err={exception.GetType().Name}", $"url={channel.Url}"]);
            }

            if (address is null)
            {
                ReleaseTransports();
                LogAttempt(index, endpoint, "unsupported_address");
                continue;
            }

            var connectTimeout = address.Scheme == Uri.UriSchemeHttp &&
                string.Equals(endpoint.Transport, "HTTP", StringComparison.OrdinalIgnoreCase)
                    ? FastMediaSorterPlaybackTransport.LanConnectTimeout : (TimeSpan?)null;
            Task<FastMediaSorterAudioOpenResult> opening;
            ExchangeRelayCredentials? relayCredentials;
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return Cancelled();
                // A proxy owns the upstream pin and password. LAN and tunnel-inner requests carry no relay password.
                relayCredentials = _relayProxy is null && endpoint.Transport?.Equals("RELAY", StringComparison.OrdinalIgnoreCase) == true
                    ? credentials : null;
                opening = _playback.StartAsync(address, volume, cancellationToken, connectTimeout,
                    _relayProxy is null ? endpoint.CertFingerprint : null, relayCredentials);
            }

            result = await opening.ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested || result.Cancelled) return Cancelled();
                if (result.Started)
                {
                    _streaming = true;
                    playedAt = index;
                }
                else
                {
                    reason = _forwarder?.LastRefusalReason ?? _relayProxy?.LastFailure
                        ?? (result.Error is ExchangeBroadcastCertificateException certificate ? certificate.Reason
                            : result.Error?.GetType().Name ?? "refused");
                    if (result.StatusCode == 401 && channel.FastMediaSorterBroadcast?.Auth is { } auth &&
                        string.Equals(auth.Scheme, ExchangeBroadcastAuth.SupportedScheme, StringComparison.Ordinal))
                    {
                        reason = relayCredentials is null
                            ? ExchangeBroadcastAuth.PasswordRequiredReason : ExchangeBroadcastAuth.PasswordRefusedReason;
                    }
                }
            }

            if (playedAt >= 0) break;
            LogAttempt(index, endpoint, reason);
            ReleaseTransports();
        }

        _log?.Invoke("FMS AUDIO RESPONSE", [$"leg={leg}", $"reconnecting={reconnecting}", $"attempt={playedAt}",
            $"http={result.StatusCode?.ToString() ?? "n/a"}", $"response_ms={result.ResponseElapsed.TotalMilliseconds:F0}",
            $"buffer_ms={FastMediaSorterAudioPlayback.BufferTargetMilliseconds}", $"url={channel.Url}"]);
        return new(playedAt >= 0, false, reason, result.StatusCode);

        void LogAttempt(int index, FastMediaSorterBroadcastEndpoint endpoint, string outcome) =>
            _log?.Invoke("FMS AUDIO ATTEMPT", [$"leg={leg}", $"reconnecting={reconnecting}", $"attempt={index}",
                $"transport={endpoint.Transport ?? "n/a"}", $"outcome={outcome}",
                $"http={result.StatusCode?.ToString() ?? "n/a"}", $"elapsed_ms={result.ResponseElapsed.TotalMilliseconds:F0}",
                $"url={endpoint.Url}"]);
    }

    private bool IsCancelled(CancellationToken token)
    {
        lock (_gate) return _disposed || token.IsCancellationRequested;
    }

    private static FastMediaSorterAudioLegResult Cancelled() => new(false, true, "cancelled", null);

    private void RelayProxyFailed(ExchangeRelayProxy proxy, string failure)
    {
        lock (_gate)
        {
            if (_disposed || !_streaming || !ReferenceEquals(_relayProxy, proxy)) return;
        }
        RelayFailed?.Invoke(failure);
    }

    private void Playback_Playing(object? sender, FastMediaSorterAudioPlaybackEventArgs e) => Playing?.Invoke(this, e);
    private void Playback_Ended(object? sender, FastMediaSorterAudioPlaybackEventArgs e) => Ended?.Invoke(this, e);
    private void Playback_Failed(object? sender, FastMediaSorterAudioPlaybackEventArgs e) => Failed?.Invoke(this, e);

    private void ReleaseTransports()
    {
        ExchangeTunnelForwarder? forwarder;
        ExchangeRelayProxy? proxy;
        lock (_gate)
        {
            forwarder = _forwarder;
            _forwarder = null;
            proxy = _relayProxy;
            _relayProxy = null;
            _streaming = false;
        }
        forwarder?.Dispose();
        proxy?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _playback.Playing -= Playback_Playing;
            _playback.Ended -= Playback_Ended;
            _playback.Failed -= Playback_Failed;
            _playback.Dispose();
        }
        ReleaseTransports();
    }
}

internal sealed record FastMediaSorterAudioLegResult(bool Started, bool Cancelled, string Reason, int? StatusCode);
