using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>One running source: its capture, its encoder, its LAN share and its exchange half.</summary>
internal sealed class BroadcastRunningSource : IDisposable
{
    public required BroadcastSource Source { get; init; }
    public required string BroadcastId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public WindowsAacEncoder? Encoder { get; init; }
    public WindowsAudioCapture? Capture { get; init; }
    public WindowsVideoBroadcast? Video { get; init; }
    public string? OpaqueToken { get; init; }
    public BroadcastSource? AudioSource { get; init; }
    public int SampleRate { get; init; } = 44100;
    public int Channels { get; init; } = 1;
    public int AudioBitrate { get; init; } = 128;
    public long AudioTime100ns { get; set; }
    public bool CaptureReady { get; set; }
    public required AdtsFrameStream Frames { get; init; }
    public BroadcastLanServer? LanServer { get; set; }
    public string? LanAddress { get; set; }
    public int LocalPort { get; set; }
    public string? DescriptorJson { get; set; }
    public bool ExchangeStarted { get; set; }
    public Channel<BroadcastMediaChunk>? UploadChunks { get; set; }
    public Task? UploadTask { get; set; }

    /// <summary>SP-0226: the keys, certificate and lease of a broadcast sealed to the account's devices; null while it is not.</summary>
    public BroadcastSealing? Sealing { get; set; }

    /// <summary>The plan the record and the outward description are written from, kept so an update repeats it.</summary>
    public BroadcastWirePlan? Plan { get; set; }

    /// <summary>The pair of the account's own devices (item BF): made with the broadcast, sealed into its record, never shown.</summary>
    public ExchangeRelayCredentials OwnPair { get; } = new(BroadcastLinkPassword.OwnUser, BroadcastLinkPassword.Generate());

    /// <summary>The pair of the outward link (item R); null when the person chose "none".</summary>
    public ExchangeRelayCredentials? LinkPair { get; set; }

    /// <summary>True for a link pair this program generated, false for one the person set.</summary>
    public bool LinkIsGenerated { get; set; } = true;

    /// <summary>Set once the broadcast carried an exchange endpoint; it stays protected for its life (item M).</summary>
    public bool Announced { get; set; }

    /// <summary>True while every endpoint demands the pair set; false for a LAN-only broadcast and for "none".</summary>
    public bool Protected { get; set; }

    /// <summary>What the relay is told on every announcement (item DV); empty exactly when nothing is protected.</summary>
    public IReadOnlyList<BroadcastVerifier> Verifiers { get; set; } = [];

    public Dictionary<ExchangeRelayCredentials, BroadcastVerifier> VerifierCache { get; } = [];

    /// <summary>The figure last announced as maxListeners; every update repeats it.</summary>
    public int AnnouncedMaxListeners { get; set; }

    /// <summary>The format header the first sealed chunk carries beside the key ring (PAT and PMT for MPEG-TS; none for ADTS).</summary>
    public byte[]? FormatHeader { get; set; }

    /// <summary>Set when the current upload has been accepted and has written its header chunk (a replacement waits for it).</summary>
    public TaskCompletionSource UploadReady { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Ends the current upload without the clean end that would end the broadcast (a replacement retires the old one this way).</summary>
    public CancellationTokenSource? UploadRetire { get; set; }
    public volatile int TunnelCount;

    public void Dispose()
    {
        Capture?.Dispose();
        Encoder?.Dispose();
    }
}

/// <summary>
/// SP-0217: the producer of this PC's own live broadcasts. One broadcast per running source, through
/// the one exchange server of the account (the owner's decision), behind the two-step consent of the
/// Settings permission and the user's explicit start (constraint 7). The capture thread owns the
/// pipeline - PCM in, ADTS frames out, one encoder output feeding the LAN share and the relay
/// (constraint 1) - and nothing it does ever blocks the interface thread (constraint 9).
/// </summary>
/// <remarks>
/// <para>Leaving and quitting end the broadcasts before `bye` and `revoke` (constraint 6), through the
/// exchange service's own leave hook. A lost exchange session is a visible state, never a background
/// retry: the producer re-establishes on the next session, with the same identity and the same
/// broadcast ids (item H - publish again after every welcome).</para>
/// <para>The listener cap is answered for the LAN share the way item M fixes it: the server cannot see
/// LAN listeners, so the producer lowers the figure the server enforces while LAN listeners are
/// connected.</para>
/// </remarks>
internal sealed partial class BroadcastProducerService : IDisposable
{
    /// <summary>The upload chunk target: whole ADTS frames aggregated to a few KiB, far under item U's 1 MiB.</summary>
    private const int UploadChunkTargetBytes = 8192;

    /// <summary>The upload queue bound: about two seconds of 128 kbit/s audio. A stalled relay ends the
    /// upload visibly (it never blocks the capture thread); no retry runs in the background.</summary>
    private const int UploadQueueChunks = 48;

    private static readonly TimeSpan StartAnswerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EndWriteTimeout = TimeSpan.FromSeconds(2);

    private readonly BroadcastProducerSettingsStore _store;
    private readonly BroadcastLinkPasswordStore _passwords;
    private readonly ExchangeSourceService _exchange;
    private readonly DriveSourceService? _drive;
    private readonly Dictionary<BroadcastSource, BroadcastRunningSource> _running = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sourceOperations = new(1, 1);
    private bool _restartHandled;

    internal BroadcastProducerSettings Settings { get; private set; }
    internal string? OutcomeKey { get; private set; }
    internal event Action? Changed;

    internal BroadcastProducerService(string dataDirectory, ExchangeSourceService exchange, DriveSourceService? drive = null)
    {
        _store = new BroadcastProducerSettingsStore(dataDirectory);
        _passwords = new BroadcastLinkPasswordStore(dataDirectory);
        Settings = _store.LoadSync();
        _exchange = exchange;
        _drive = drive;
        _exchange.SessionOnline += OnSessionOnline;
        _exchange.SessionOffline += OnSessionOffline;
        _exchange.CastResult += OnCastResult;
        _exchange.TunnelRequested += OnTunnelRequested;
        _exchange.DirectoryChanged += OnDirectoryChanged;
        _exchange.BeforeLeaveAsync = StopAllAsync;
    }

    internal IReadOnlyList<BroadcastSource> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _running.Keys];
            }
        }
    }

    /// <summary>The consent gate the UI asks before offering a start; the service asks it again itself.</summary>
    internal BroadcastStartRefusal? RefusalFor(BroadcastSource source)
    {
        lock (_gate)
        {
            return BroadcastProducerGate.RefusalFor(Settings, source, _running.Keys.ToHashSet(), IsGuestOnly);
        }
    }

    /// <summary>
    /// The user's explicit start of one source (requirement 1). The LAN share and the capture start
    /// without the server; the exchange half starts when the session is up, and follows it back.
    /// </summary>
    internal async Task<bool> StartAsync(BroadcastSource source)
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try { return await StartCoreAsync(source).ConfigureAwait(false); }
        finally { _sourceOperations.Release(); }
    }

    private async Task<bool> StartCoreAsync(BroadcastSource source)
    {
        var refusal = RefusalFor(source);
        if (refusal is not null)
        {
            Report(RefusalKey(refusal.Value));
            return false;
        }

        var lanAddress = Settings.LanListening ? ChooseLanAddress() : null;
        if (!BroadcastProducerGate.IsReachable(lanAddress, _exchange.PublicEndpoint is not null,
            _exchange.MayBroadcast, _exchange.ServerSeals, _exchange.OwnKeyMissing, _exchange.Features))
        {
            Report("BroadcastNoReachableEndpoint");
            return false;
        }

        if (!BroadcastSources.IsAudioOnly(source)) return await StartVideoAsync(source, lanAddress).ConfigureAwait(false);
        var port = SourcePort(source);
        if (port == 0) { Report("BroadcastPortTaken"); return false; }

        var channels = source == BroadcastSource.Microphone ? Settings.MicrophoneChannels : Settings.SystemSoundChannels;
        WindowsAacEncoder encoder;
        try
        {
            encoder = await Task.Run(() => new WindowsAacEncoder(Settings.SampleRate, channels, Settings.BitrateKbps)).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Constraint 10: no new binary for the encoder; where Windows' own is absent the source is
            // not offered and the settings say why.
            Report("BroadcastEncoderMissing");
            return false;
        }

        var lanServer = new BroadcastLanServer(new BroadcastLanServerOptions(
            TitleOf(source), Settings.BitrateKbps, Settings.MaxListeners,
            [ "/" + BroadcastDescriptorWriter.AudioLanPath, "/" + BroadcastDescriptorWriter.AudioLanPathAlias ],
            BroadcastDescriptorWriter.AudioContentType, IsVideo: false));
        try
        {
            // The loopback half is always bound - the tunnel bridge and this machine reach it there;
            // the LAN half only while the opt-in is on (constraint 4).
            lanServer.Start(lanAddress, port);
        }
        catch (SocketException)
        {
            // Section 2.1: no fallback port - taken means this share does not start.
            await lanServer.StopAsync().ConfigureAwait(false);
            encoder.Dispose();
            Report("BroadcastPortTaken");
            return false;
        }

        var state = new BroadcastRunningSource
        {
            Source = source,
            BroadcastId = ExchangeProtocol.NewDeviceId(),
            Title = TitleOf(source),
            StartedAt = DateTimeOffset.Now,
            Encoder = encoder,
            AudioSource = source,
            SampleRate = Settings.SampleRate,
            Channels = channels,
            AudioBitrate = Settings.BitrateKbps,
            CaptureReady = true,
            Capture = new WindowsAudioCapture(source == BroadcastSource.SystemSound,
                source == BroadcastSource.Microphone ? Settings.MicrophoneDeviceId : null,
                Settings.SampleRate, channels),
            Frames = new AdtsFrameStream(),
            LanServer = lanServer,
            LanAddress = lanAddress,
            LocalPort = port
        };
        state.LinkPair = NewLinkPair(out var generatedLink);
        state.LinkIsGenerated = generatedLink;
        await ApplyProtectionAsync(state).ConfigureAwait(false);
        state.Plan = PlanOf(state, null, relay: false, tunnel: false, BroadcastSealMode.None);
        state.DescriptorJson = BroadcastDescriptorWriter.Write(state.Plan);
        state.Capture.PcmReady += pcm => OnPcm(state, pcm);
        state.Capture.Failed += failure => OnCaptureFailed(state, failure);
        lanServer!.LanListenerCountChanged += count => OnLanListenerCount(state, count);

        lock (_gate)
        {
            _running[source] = state;
        }

        if (!Settings.Allowed || IsGuestOnly) { await StopCoreAsync(source).ConfigureAwait(false); return false; }
        state.Capture.Start();
        await SaveChosenAsync(source, chosen: true).ConfigureAwait(false);
        Changed?.Invoke();
        await EstablishOnExchangeAsync(state).ConfigureAwait(false);
        if (lanAddress is null && !state.ExchangeStarted)
        {
            await StopCoreAsync(source).ConfigureAwait(false);
            return false;
        }
        await PublishDriveAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>The user's stop of one source: the broadcast ends cleanly, the choice is unmade.</summary>
    internal async Task StopAsync(BroadcastSource source)
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync(source).ConfigureAwait(false); }
        finally { _sourceOperations.Release(); }
    }

    private async Task StopCoreAsync(BroadcastSource source)
    {
        BroadcastRunningSource? state;
        lock (_gate)
        {
            if (!_running.Remove(source, out state))
            {
                return;
            }
        }

        await TeardownAsync(state).ConfigureAwait(false);
        await PublishDriveAsync().ConfigureAwait(false);
        await SaveChosenAsync(source, chosen: false).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Ends every running broadcast: the leave path (constraint 6 - `broadcast-end` while the control
    /// stream is still alive, before `bye` and `revoke`) and the quit path both call this.
    /// </summary>
    internal async Task StopAllAsync()
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try { await StopAllCoreAsync().ConfigureAwait(false); }
        finally { _sourceOperations.Release(); }
    }

    private async Task StopAllCoreAsync()
    {
        List<BroadcastRunningSource> states;
        lock (_gate)
        {
            states = [.. _running.Values];
            foreach (var state in states)
            {
                _running.Remove(state.Source);
            }
        }

        foreach (var state in states)
        {
            await TeardownAsync(state).ConfigureAwait(false);
        }
        await PublishDriveAsync().ConfigureAwait(false);

        if (states.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _exchange.SessionOnline -= OnSessionOnline;
        _exchange.SessionOffline -= OnSessionOffline;
        _exchange.CastResult -= OnCastResult;
        _exchange.TunnelRequested -= OnTunnelRequested;
        _exchange.DirectoryChanged -= OnDirectoryChanged;
        _exchange.BeforeLeaveAsync = null;
    }

    // --- settings -----------------------------------------------------------------

    internal async Task UpdateSettingsAsync(Func<BroadcastProducerSettings, BroadcastProducerSettings> change)
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = Settings;
            Settings = change(Settings);
            await _store.SaveAsync(Settings).ConfigureAwait(false);
            if (previous.LanPort != Settings.LanPort) await StopAllCoreAsync().ConfigureAwait(false);
            else if (previous.LanListening != Settings.LanListening) await ReconfigureLanAsync().ConfigureAwait(false);
            Changed?.Invoke();
        }
        finally { _sourceOperations.Release(); }
    }

    /// <summary>
    /// The permission of the two-step consent. Roles are declared at enroll, never at hello;
    /// changing this setting does not change the roles of an already enrolled entry.
    /// </summary>
    internal async Task SetAllowedAsync(bool allowed)
    {
        await UpdateSettingsAsync(settings => settings with { Allowed = allowed }).ConfigureAwait(false);
        if (!allowed) await StopAllAsync().ConfigureAwait(false);
        if (allowed && _exchange.IsActive)
        {
            // Re-establish the session; the server still holds the roles from enrollment.
            await _exchange.SetEnabledAsync(true).ConfigureAwait(false);
        }
    }

    /// <summary>The hand-off text of a running broadcast: one descriptor behind every channel (requirement 3).</summary>
    internal string? DescriptorText(BroadcastSource source)
    {
        lock (_gate)
        {
            return _running.TryGetValue(source, out var state) ? OutwardDescriptor(state) : null;
        }
    }

    // --- capture pipeline (the capture thread) ------------------------------------

    private void OnPcm(BroadcastRunningSource state, short[] pcm)
    {
        var encoded = state.Encoder!.Encode(MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray());
        if (encoded.Length == 0)
        {
            return;
        }

        state.Frames.Append(encoded);
        var uploadBuffer = new List<byte>(UploadChunkTargetBytes * 2);
        while (state.Frames.TryReadFrame(out var frame))
        {
            if (state.Video is not null)
            {
                var chunk = state.Video.Audio(frame.ToArray(), state.AudioTime100ns);
                state.AudioTime100ns += 1024L * 10_000_000 / state.SampleRate;
                OnVideoChunk(state, chunk);
                continue;
            }
            // One encoder output feeds every listener: the LAN share gets whole frames, and the relay's
            // chunk is whole frames too, so a late joiner always starts at a frame boundary.
            state.LanServer?.Enqueue(frame);
            uploadBuffer.AddRange(frame.ToArray());
            if (uploadBuffer.Count >= UploadChunkTargetBytes)
            {
                PostUploadChunk(state, uploadBuffer.ToArray());
                uploadBuffer.Clear();
            }
        }

        if (uploadBuffer.Count > 0)
        {
            PostUploadChunk(state, uploadBuffer.ToArray());
        }
    }

    /// <summary>Hands one chunk to the upload task; a queue that is full ends the upload visibly, never blocks the capture.</summary>
    private void PostUploadChunk(BroadcastRunningSource state, byte[] chunk)
        => PostUploadChunk(state, new BroadcastMediaChunk(chunk, true));

    private void PostUploadChunk(BroadcastRunningSource state, BroadcastMediaChunk chunk)
    {
        if (state.UploadChunks is not null && !state.UploadChunks.Writer.TryWrite(chunk))
        {
            state.UploadChunks.Writer.TryComplete();
            state.UploadChunks = null;
            Report("BroadcastRelayLost");
        }
    }

    private void OnCaptureFailed(BroadcastRunningSource state, BroadcastCaptureFailure failure)
    {
        Report(CaptureFailureKey(failure));
        _ = StopAsync(state.Source);
    }

    internal static string CaptureFailureKey(BroadcastCaptureFailure failure) => failure switch
    {
        BroadcastCaptureFailure.WindowsPermissionDenied => "BroadcastPermissionDenied",
        BroadcastCaptureFailure.DeviceInUse => "BroadcastDeviceInUse",
        BroadcastCaptureFailure.DeviceMissing => "BroadcastDeviceMissing",
        _ => "BroadcastCaptureFailed"
    };

    // --- the exchange half --------------------------------------------------------

    private async Task EstablishOnExchangeAsync(BroadcastRunningSource state)
    {
        var publicEndpoint = _exchange.PublicEndpoint;
        if (publicEndpoint is null || !_exchange.MayBroadcast)
        {
            // No session: the LAN share keeps serving; the next session online brings the exchange half.
            // An account that is a guest's announces nothing on the server (item AO): the LAN share and the
            // link are all it has.
            return;
        }

        var relay = _exchange.Features.Contains("relay", StringComparer.Ordinal);
        var tunnel = _exchange.Features.Contains("tunnel", StringComparer.Ordinal);

        // SP-0226 / items DF and DP: nothing goes through a server that cannot carry sealed content, and an entry
        // with no key of its own seals nothing. The LAN share keeps serving and its description can still be handed
        // out - the person is told why the exchange half is absent, never left to find out.
        if (!_exchange.ServerSeals || _exchange.OwnKeyMissing)
        {
            state.Plan = PlanOf(state, null, relay: false, tunnel: false, BroadcastSealMode.None);
            state.DescriptorJson = BroadcastDescriptorWriter.Write(state.Plan);
            Report(_exchange.OwnKeyMissing ? "ExchangeE2eOwnKeyMissing" : ExchangeSealedState.ServerNoE2e.MessageKey());
            return;
        }

        // Item M: from the moment it carries an exchange endpoint the broadcast is protected - for the rest of its life.
        state.Announced = relay || tunnel;
        await ApplyProtectionAsync(state).ConfigureAwait(false);
        var sealing = await PrepareSealingAsync(state).ConfigureAwait(false);
        if (sealing is null)
        {
            return;
        }
        await sealing.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var plan = PlanOf(state, publicEndpoint, relay, tunnel, BroadcastSealMode.Record);
            var descriptor = BroadcastDescriptorWriter.Write(plan);
            state.Plan = plan;
            state.DescriptorJson = descriptor;
            state.AnnouncedMaxListeners = Settings.MaxListeners;
            if (SealRecordBox(state, sealing) is null)
            {
                Report(ExchangeSealedState.E2eMissing.MessageKey());
                return;
            }

            // Constraint 3: the broadcast starts once the server answered `broadcast-started`, with or
            // without a LAN address. A refusal is the user's message, never a stalled start.
            var answerTask = _exchange.WaitForBroadcastStartAnswerAsync(state.BroadcastId, StartAnswerTimeout, CancellationToken.None);
            var posted = await PostRecordAsync(state, "broadcast-start", CancellationToken.None).ConfigureAwait(false);
            if (!posted)
            {
                Report("ExchangeUnavailable");
                return;
            }

            string? answer;
            try
            {
                answer = await answerTask.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Report("ExchangeUnavailable");
                return;
            }

            if (answer is not null)
            {
                Report(ExchangeProtocol.RefusalKey(answer));
                return;
            }

            state.ExchangeStarted = true;
            if (relay && state.CaptureReady)
            {
                // Every upload replacement, including a reconnect, carries a new epoch (item DL).
                if (state.Sealing?.Sealer.Relay is { } encoder)
                {
                    encoder.NewEpoch();
                }
                StartUpload(state);
            }
        }
        finally
        {
            sealing.Gate.Release();
        }
        await PublishDriveAsync().ConfigureAwait(false);
    }
    /// <summary>The relay upload: a second TLS data stream - `hello`, `upload`, chunks - owned by this task.</summary>
    private void StartUpload(BroadcastRunningSource state)
    {
        if (state.UploadChunks is not null)
        {
            return;
        }

        var chunks = Channel.CreateBounded<BroadcastMediaChunk>(state.Video is null ? UploadQueueChunks : 8);
        var retire = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.UploadChunks = chunks;
        state.UploadRetire = retire;
        state.UploadReady = ready;
        state.UploadTask = RunUploadAsync(state, chunks, retire.Token, ready);
    }

    private async Task RunUploadAsync(BroadcastRunningSource state, Channel<BroadcastMediaChunk> chunks, CancellationToken retire,
        TaskCompletionSource ready)
    {
        var account = _exchange.Account;
        var sealing = state.Sealing;
        try
        {
            using var connection = await ExchangeTlsConnection
                .OpenAsync(account.Host, account.Port, account.Pin, CancellationToken.None, account.PinKey)
                .ConfigureAwait(false);
            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "hello", deviceId = account.DeviceId, deviceToken = account.Token,
                productVersion = ProductInfo.Version, keepaliveSeconds = 30
            }, false, CancellationToken.None).ConfigureAwait(false);
            using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", CancellationToken.None).ConfigureAwait(false);
            if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
            {
                Report(ExchangeProtocol.RefusalKey(ExchangeProtocol.String(welcome.RootElement, "reason")));
                return;
            }

            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                // Item DL: a sealed upload declares the sealed type; the true media type travels inside the key ring.
                schemaVersion = 2, type = "upload", broadcastId = state.BroadcastId,
                contentType = sealing is null ? MediaTypeOf(state.Source) : ExchangeSealedRelayRecords.ContentType
            }, true, CancellationToken.None).ConfigureAwait(false);
            using var accepted = await ExchangeHandshake.ReadAsync(connection.Stream, "upload-accepted", CancellationToken.None).ConfigureAwait(false);
            if (ExchangeProtocol.String(accepted.RootElement, "type") == "refused")
            {
                Report(ExchangeProtocol.RefusalKey(ExchangeProtocol.String(accepted.RootElement, "reason")));
                return;
            }

            // A sealed upload always begins with its header chunk (the key ring, then the format header's record when the
            // format has one) and then carries one media record per chunk (item DL). Every audio chunk is whole ADTS frames
            // and joinable (7.6): a listener may start at any one.
            await ExchangeSealedUpload.WriteAsync(connection.Stream, sealing?.Sealer.Relay, state.FormatHeader ?? [],
                chunks.Reader.ReadAllAsync(CancellationToken.None), () => ready.TrySetResult(), retire).ConfigureAwait(false);

            // The clean end of 7.6: a length of zero.
            await ExchangeUploadChunks.WriteEndAsync(connection.Stream, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (retire.IsCancellationRequested)
        {
            // A replacement upload took over (item N): this stream just closes, with no end that would end the broadcast.
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // The upload ends; the LAN share keeps serving. The next session online re-establishes the
            // exchange half; no retry runs in the background (constraint 6).
            Report("BroadcastRelayLost");
        }
        finally
        {
            ready.TrySetResult();
        }
    }
    private void OnSessionOnline()
    {
        // Item H: publish again after every welcome - same ids, so the record is replaced, not doubled.
        List<BroadcastRunningSource> states;
        lock (_gate)
        {
            states = [.. _running.Values];
        }

        foreach (var state in states)
        {
            _ = EstablishOnExchangeAsync(state);
        }

        if (!_restartHandled)
        {
            // Requirement 7: with the restart setting on, the sources the user chose earlier start once
            // the account is online; with it off nothing starts. Nothing else ever starts a source.
            _restartHandled = true;
            foreach (var source in BroadcastProducerGate.RestartSources(Settings))
            {
                _ = StartAsync(source);
            }
        }
    }

    private void OnSessionOffline()
    {
        List<BroadcastRunningSource> states;
        lock (_gate)
        {
            states = [.. _running.Values];
        }

        foreach (var state in states)
        {
            state.ExchangeStarted = false;
            state.UploadChunks?.Writer.TryComplete();
            state.UploadChunks = null;
        }
    }

    private void OnLanListenerCount(BroadcastRunningSource state, int lanListeners)
    {
        // Item M: the server counts the relay and the tunnels; the producer answers for the LAN share,
        // lowering the figure while LAN listeners are connected.
        if (Settings.MaxListeners <= 0 || !state.ExchangeStarted || state.DescriptorJson is null)
        {
            return;
        }

        state.AnnouncedMaxListeners = Math.Max(0, Settings.MaxListeners - lanListeners);
        using var timeout = new CancellationTokenSource(EndWriteTimeout);
        // The update repeats the record whole, box included: a record without it would read as unsealed (item DK).
        _ = PostRecordAsync(state, "broadcast-update", timeout.Token);
    }
    private async Task TeardownAsync(BroadcastRunningSource state)
    {
        if (state.Video is not null) await state.Video.DisposeAsync().ConfigureAwait(false);
        await Task.Run(state.Dispose).ConfigureAwait(false);
        // The upload ends before the record does: the zero-length chunk closes the relay cleanly, then
        // the broadcast itself ends on the control stream.
        state.UploadChunks?.Writer.TryComplete();
        state.UploadChunks = null;
        if (state.UploadTask is { } upload)
        {
            try
            {
                await upload.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                // The upload stream was already gone; nothing more is owed to it.
            }
        }

        state.Sealing?.Dispose();
        state.Sealing = null;
        if (state.ExchangeStarted)
        {
            using var timeout = new CancellationTokenSource(EndWriteTimeout);
            _ = await _exchange.PostControlEnvelopeAsync(new
            {
                schemaVersion = 2, type = "broadcast-end", broadcastId = state.BroadcastId
            }, timeout.Token).ConfigureAwait(false);
        }

        if (state.LanServer is not null)
        {
            // Every listener's chunked body ends cleanly.
            await state.LanServer.StopAsync().ConfigureAwait(false);
            state.LanServer = null;
        }
    }

    // --- cast (the caster's side, 7.8) ----------------------------------------------

    /// <summary>Casts one running broadcast to one named receiver of the account - the user's act, never automatic.</summary>
    internal async Task<bool> CastAsync(BroadcastSource source, ExchangeDeviceRecord receiver)
    {
        BroadcastRunningSource? state;
        lock (_gate)
        {
            if (!_running.TryGetValue(source, out state) || state.DescriptorJson is null)
            {
                return false;
            }
        }

        // A cast carries no secret (requirement 5): the envelope is two ids, and the server builds the
        // offer from the record it already holds. A receiver whose declared pairs cover none of this
        // broadcast's endpoints is never sent one - the UI shows it as unsupported, and this is the
        // wire's own gate, so the two cannot disagree.
        if (receiver.DeviceId == _exchange.Account.DeviceId || !state.ExchangeStarted || state.Sealing is null)
        {
            return false;
        }

        using var lease = _exchange.AcquireDirectory(follow: false);
        if (!await _exchange.WaitForDirectoryAsync(DirectoryWait).ConfigureAwait(false)
            || !await RefreshSealingAsync(state, ensureWritten: true).ConfigureAwait(false)
            || state.Sealing.Sealer.Box?.Keys.Any(key => key.To == receiver.DeviceId) != true)
        {
            Report("BroadcastCastUnsupported");
            return false;
        }

        var descriptor = FastMediaSorterBroadcastDescriptor.Read(state.DescriptorJson);
        var endpoints = descriptor is { IsAccepted: true, Broadcast: not null } ? descriptor.Broadcast.Endpoints : [];
        if (!ExchangeCastEligibility.CanOffer(receiver.Plays, endpoints))
        {
            Report("BroadcastCastUnsupported");
            return false;
        }

        using var timeout = new CancellationTokenSource(StartAnswerTimeout);
        return await _exchange.PostControlEnvelopeAsync(new
        {
            schemaVersion = 2, type = "cast", broadcastId = state.BroadcastId, receiverDeviceId = receiver.DeviceId
        }, timeout.Token).ConfigureAwait(false);
    }

    internal async Task StopCastAsync(BroadcastSource source, string receiverDeviceId)
    {
        BroadcastRunningSource? state;
        lock (_gate)
        {
            if (!_running.TryGetValue(source, out state))
            {
                return;
            }
        }

        using var timeout = new CancellationTokenSource(EndWriteTimeout);
        _ = await _exchange.PostControlEnvelopeAsync(new
        {
            schemaVersion = 2, type = "cast-stop", broadcastId = state.BroadcastId, receiverDeviceId
        }, timeout.Token).ConfigureAwait(false);
    }

    private void OnCastResult(ExchangeCastResult result) => Report(result.MessageKey);

    // --- tunnels (the producer's side of 7.5) ---------------------------------------

    private void OnTunnelRequested(ExchangeTunnelOpen tunnel, CancellationToken cancellationToken)
    {
        BroadcastRunningSource? state = null;
        lock (_gate)
        {
            foreach (var running in _running.Values)
            {
                if (running.BroadcastId == tunnel.BroadcastId)
                {
                    state = running;
                    break;
                }
            }
        }

        if (state?.Sealing is null)
        {
            // Item DP: no clear fallback through the server. A tunnel the server asks for a broadcast that is not sealed
            // is not bridged.
            return;
        }

        // Item L: at most 16 tunnels per broadcast; one over the bound is dropped, not queued.
        if (Interlocked.Increment(ref state.TunnelCount) > ExchangeTunnelOpen.TunnelsPerBroadcast)
        {
            _ = Interlocked.Decrement(ref state.TunnelCount);
            return;
        }

        _ = ExchangeTunnelBridge.RunAsync(_exchange, tunnel, state.LocalPort, cancellationToken, state.Sealing.Sealer.Tunnel)
            .ContinueWith(_ => Interlocked.Decrement(ref state.TunnelCount), CancellationToken.None);
    }

    // --- helpers -------------------------------------------------------------------

    private async Task SaveChosenAsync(BroadcastSource source, bool chosen)
    {
        var updated = Settings with { RestartSources = BroadcastProducerGate.ChosenAfter(Settings, source, chosen) };
        await _store.SaveAsync(updated).ConfigureAwait(false);
        Settings = updated;
    }

    private void Report(string key)
    {
        OutcomeKey = key;
        Changed?.Invoke();
    }

    private static string RefusalKey(BroadcastStartRefusal refusal) => refusal switch
    {
        BroadcastStartRefusal.NotAllowed => "BroadcastNotAllowed",
        BroadcastStartRefusal.TooManySources => "BroadcastTooManySources",
        BroadcastStartRefusal.GuestAccount => "BroadcastGuestRefused",
        _ => "BroadcastAlreadyRunning"
    };

    private static string TitleOf(BroadcastSource source) => source switch
    {
        BroadcastSource.Microphone => LocalizationService.Get("BroadcastMicTitle"),
        BroadcastSource.SystemSound => LocalizationService.Get("BroadcastSystemSoundTitle"),
        BroadcastSource.Screen => LocalizationService.Get("BroadcastScreenTitle"),
        BroadcastSource.Camera => LocalizationService.Get("BroadcastCameraTitle"),
        _ => LocalizationService.Get("BroadcastMicTitle")
    };

    /// <summary>A reachable LAN IPv4 of an up interface - never loopback, never the link-local automatic one.</summary>
    private static string? ChooseLanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic is not { OperationalStatus: OperationalStatus.Up, NetworkInterfaceType: not NetworkInterfaceType.Loopback })
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(address.Address)
                    || address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                return address.Address.ToString();
            }
        }

        return null;
    }

    private static bool IsConnectionFailure(Exception exception) => exception is IOException or SocketException
        or OperationCanceledException or TimeoutException or JsonException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException or InvalidDataException or ObjectDisposedException
        or AuthenticationException or InvalidOperationException;
}
