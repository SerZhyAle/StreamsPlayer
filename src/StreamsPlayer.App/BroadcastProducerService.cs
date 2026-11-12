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
    public bool UploadAccepted { get; set; }
    public bool UploadJoined { get; set; }
    public List<BroadcastMediaChunk> UploadStartupBuffer { get; } = [];
    public object UploadGate { get; } = new();

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
    public string? FailureKey { get; set; }
    public BroadcastCaptureFailure? CaptureFailure { get; set; }
    public string? AnnouncedServer { get; set; }
    public IReadOnlyList<string> RecipientNames { get; set; } = [];
    public int LastLanListeners { get; set; }

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
    private const int VideoUploadQueueChunks = 128;

    private static readonly TimeSpan StartAnswerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EndWriteTimeout = TimeSpan.FromSeconds(2);

    /// <summary>S5-1: the relay upload's connect and hello/upload handshake, the same 15 s the exchange's own handshakes get.</summary>
    private static readonly TimeSpan UploadHandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly BroadcastProducerSettingsStore _store;
    private readonly BroadcastLinkPasswordStore _passwords;
    private readonly ExchangeSourceService _exchange;
    private readonly DriveSourceService? _drive;
    private readonly Dictionary<BroadcastSource, BroadcastRunningSource> _running = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sourceOperations = new(1, 1);
    private bool _restartHandled;
    private long _exchangeGeneration;

    internal BroadcastProducerSettings Settings { get; private set; }
    internal string? OutcomeKey { get; private set; }
    internal event Action? Changed;

    internal BroadcastProducerService(string dataDirectory, ExchangeSourceService exchange, DriveSourceService? drive = null, Action<string>? diagnostic = null)
    {
        _diagnostic = diagnostic ?? HandlerBoundary.Diagnostic;
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
        _exchange.BeforeLeaveAsync = () => StopAllAsync(BroadcastStopCause.SignOut);
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
    internal async Task<bool> StartAsync(BroadcastSource source, CancellationToken cancel = default)
    {
        CancellationTokenSource own;
        lock (_gate)
        {
            if (!_starting.Add(source)) return false;
            own = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            _startCancels[source] = own;
        }
        SetVerdict(new(source, BroadcastProducerState.Starting));
        Diagnose(source, "start-asked");
        var token = own.Token;
        var holdsOperations = false;
        try
        {
            // SP-0252: the wait for another source's start is part of this start, so the person's cancel ends it too.
            await _sourceOperations.WaitAsync(token).ConfigureAwait(false);
            holdsOperations = true;
            OutcomeKey = null;
            var started = await StartCoreAsync(source, token).ConfigureAwait(false);
            BroadcastRunningSource? state;
            lock (_gate) _running.TryGetValue(source, out state);
            if (started && state is not null && state.CaptureFailure is null)
            {
                UpdateRouteVerdict(state, started: true);
                return true;
            }
            if (state is not null && state.CaptureFailure is not null)
                await StopCoreAsync(source, BroadcastStopCause.CaptureLoss).ConfigureAwait(false);
            if (StartVerdicts.FirstOrDefault(item => item.Source == source)?.State == BroadcastProducerState.Failed) return false;
            return FailStart(source, state?.FailureKey ?? OutcomeKey ?? "ExchangeUnavailable");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // SP-0252 / APP-BEHAVIOUR: the person cancelled a start. Whatever it had opened is closed, and the source
            // is simply off again - a cancel is not a failure and leaves no sentence to read.
            Diagnose(source, "start-cancelled");
            if (holdsOperations) await StopCoreAsync(source, BroadcastStopCause.StartRollback).ConfigureAwait(false);
            SetVerdict(new(source, BroadcastProducerState.Stopped));
            return false;
        }
        catch (Exception exception) when (IsConnectionFailure(exception) || WindowsVideoBroadcast.IsCaptureFailure(exception))
        {
            Diagnose(source, "start-failed", "type=" + exception.GetType().Name);
            await StopCoreAsync(source, BroadcastStopCause.StartRollback).ConfigureAwait(false);
            return FailStart(source, "BroadcastCaptureFailed");
        }
        finally
        {
            lock (_gate)
            {
                _starting.Remove(source);
                _startCancels.Remove(source);
            }
            own.Dispose();
            if (holdsOperations) _sourceOperations.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// SP-0252 requirement 2: ends a start that is still waiting - for another source's start, the account's device list,
    /// the server's answer or the device. A source that is already live is not touched; stopping it is <see cref="StopAsync"/>.
    /// </summary>
    internal void CancelStart(BroadcastSource source)
    {
        CancellationTokenSource? start;
        lock (_gate) _startCancels.TryGetValue(source, out start);
        try { start?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The start finished between the lookup and the cancel: there is nothing left to cancel.
        }
    }

    private async Task<bool> StartCoreAsync(BroadcastSource source, CancellationToken cancel)
    {
        var refusal = RefusalFor(source);
        if (refusal is not null)
        {
            Diagnose(source, "consent-refused", "code=" + RefusalKey(refusal.Value));
            return FailStart(source, RefusalKey(refusal.Value));
        }

        var lanAddress = Settings.LanListening ? ChooseLanAddress() : null;
        var reachable = BroadcastProducerGate.IsReachable(lanAddress, _exchange.PublicEndpoint is not null,
            _exchange.MayBroadcast, _exchange.ServerSeals, _exchange.OwnKeyMissing, _exchange.Features);
        Diagnose(source, "reachability", ReachabilityFacts(lanAddress, reachable));
        if (!reachable) return FailStart(source, "BroadcastNoReachableEndpoint");

        if (!BroadcastSources.IsAudioOnly(source)) return await StartVideoAsync(source, lanAddress, cancel).ConfigureAwait(false);
        var port = SourcePort(source);
        if (port == 0) return FailStart(source, "BroadcastPortTaken");

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
            return FailStart(source, "BroadcastEncoderMissing");
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
            LogBinds(source, lanAddress, port);
        }
        catch (SocketException)
        {
            // Section 2.1: no fallback port - taken means this share does not start.
            await lanServer.StopAsync().ConfigureAwait(false);
            encoder.Dispose();
            return FailStart(source, "BroadcastPortTaken");
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
            CaptureReady = false,
            Capture = new WindowsAudioCapture(source == BroadcastSource.SystemSound,
                source == BroadcastSource.Microphone ? Settings.MicrophoneDeviceId : null,
                Settings.SampleRate, channels),
            Frames = new AdtsFrameStream(),
            LanServer = lanServer,
            LanAddress = lanAddress,
            LocalPort = port
        };
        lock (_gate) _running[source] = state;
        state.LinkPair = NewLinkPair(out var generatedLink);
        state.LinkIsGenerated = generatedLink;
        await ApplyProtectionAsync(state).ConfigureAwait(false);
        state.Plan = PlanOf(state, null, relay: false, tunnel: false, BroadcastSealMode.None);
        state.DescriptorJson = BroadcastDescriptorWriter.Write(state.Plan);
        state.Capture.PcmReady += pcm => OnPcm(state, pcm);
        state.Capture.Failed += failure => OnCaptureFailed(state, failure);
        lanServer!.LanListenerCountChanged += count => OnLanListenerCount(state, count);

        if (!Settings.Allowed || IsGuestOnly)
        {
            await StopCoreAsync(source, BroadcastStopCause.StartRollback).ConfigureAwait(false);
            return FailStart(source, IsGuestOnly ? "BroadcastGuestRefused" : "BroadcastNotAllowed");
        }
        cancel.ThrowIfCancellationRequested();
        var captureFailure = await state.Capture.StartAsync().ConfigureAwait(false);
        if (captureFailure is not null)
        {
            await StopCoreAsync(source, BroadcastStopCause.CaptureLoss).ConfigureAwait(false);
            return FailStart(source, CaptureFailureKey(captureFailure.Value));
        }
        state.CaptureReady = true;
        Diagnose(source, "capture-opened");
        await SaveChosenAsync(source, chosen: true).ConfigureAwait(false);
        Changed?.Invoke();
        await EstablishOnExchangeCoreAsync(state, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        if (lanAddress is null && !state.ExchangeStarted)
        {
            await StopCoreAsync(source, BroadcastStopCause.StartRollback).ConfigureAwait(false);
            return FailStart(source, state.FailureKey ?? "ExchangeUnavailable");
        }
        await PublishDriveAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>The user's stop of one source: the broadcast ends cleanly, the choice is unmade.</summary>
    internal async Task StopAsync(BroadcastSource source, BroadcastStopCause cause = BroadcastStopCause.Person)
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync(source, cause).ConfigureAwait(false); }
        finally { _sourceOperations.Release(); }
    }

    private async Task StopCoreAsync(BroadcastSource source, BroadcastStopCause cause = BroadcastStopCause.StartRollback)
    {
        BroadcastRunningSource? state;
        lock (_gate)
        {
            if (!_running.Remove(source, out state))
            {
                return;
            }
        }

        Diagnose(source, "stop", "cause=" + StopCauseName(cause));
        if (cause == BroadcastStopCause.CaptureLoss)
            SetVerdict(new(source, BroadcastProducerState.Failed, state.FailureKey ?? "BroadcastCaptureFailed"));
        else if (cause != BroadcastStopCause.StartRollback)
            SetVerdict(new(source, BroadcastProducerState.Stopped));
        await TeardownAsync(state).ConfigureAwait(false);
        await PublishDriveAsync().ConfigureAwait(false);
        await SaveChosenAsync(source, chosen: false).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Ends every running broadcast: the leave path (constraint 6 - `broadcast-end` while the control
    /// stream is still alive, before `bye` and `revoke`) and the quit path both call this.
    /// </summary>
    internal async Task StopAllAsync(BroadcastStopCause cause = BroadcastStopCause.Person)
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try { await StopAllCoreAsync(cause).ConfigureAwait(false); }
        finally { _sourceOperations.Release(); }
    }

    private async Task StopAllCoreAsync(BroadcastStopCause cause = BroadcastStopCause.SettingsChanged)
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
            Diagnose(state.Source, "stop", "cause=" + StopCauseName(cause));
            SetVerdict(new(state.Source, BroadcastProducerState.Stopped));
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
        if (!allowed) await StopAllAsync(BroadcastStopCause.SettingsChanged).ConfigureAwait(false);
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
        Channel<BroadcastMediaChunk>? chunks;
        lock (state.UploadGate)
        {
            chunks = state.UploadChunks;
            if (chunks is null)
            {
                return;
            }

            var isVideo = !BroadcastSources.IsAudioOnly(state.Source);
            if (!state.UploadAccepted)
            {
                if (isVideo)
                {
                    if (chunk.Joinable)
                    {
                        state.UploadStartupBuffer.Clear();
                        state.UploadStartupBuffer.Add(chunk);
                    }
                    else if (state.UploadStartupBuffer.Count > 0 && state.UploadStartupBuffer.Count < VideoUploadQueueChunks)
                    {
                        state.UploadStartupBuffer.Add(chunk);
                    }
                }
                else
                {
                    state.UploadStartupBuffer.Add(chunk);
                    if (state.UploadStartupBuffer.Count > UploadQueueChunks)
                    {
                        state.UploadStartupBuffer.RemoveAt(0);
                    }
                }
                return;
            }

            if (!state.UploadJoined)
            {
                if (isVideo && !chunk.Joinable)
                {
                    return;
                }
                state.UploadJoined = true;
            }
        }

        if (!chunks.Writer.TryWrite(chunk))
        {
            lock (state.UploadGate)
            {
                if (state.UploadChunks == chunks)
                {
                    state.UploadChunks.Writer.TryComplete();
                    state.UploadChunks = null;
                }
            }
            Diagnose(state.Source, "relay-upload-lost", "code=queue-full");
            ReportState(state, "BroadcastRelayLost");
        }
    }

    private void OnCaptureFailed(BroadcastRunningSource state, BroadcastCaptureFailure failure)
    {
        lock (_gate)
        {
            if (!IsRunning(state)) return;
            state.CaptureFailure = failure;
        }
        Diagnose(state.Source, "capture-lost", "code=" + CaptureFailureKey(failure));
        ReportState(state, CaptureFailureKey(failure));
        _ = StopCaptureAsync(state);
    }

    private async Task StopCaptureAsync(BroadcastRunningSource state)
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning(state)) await StopCoreAsync(state.Source, BroadcastStopCause.CaptureLoss).ConfigureAwait(false);
        }
        finally { _sourceOperations.Release(); }
    }

    internal static string CaptureFailureKey(BroadcastCaptureFailure failure) => failure switch
    {
        BroadcastCaptureFailure.WindowsPermissionDenied => "BroadcastPermissionDenied",
        BroadcastCaptureFailure.DeviceInUse => "BroadcastDeviceInUse",
        BroadcastCaptureFailure.DeviceMissing => "BroadcastDeviceMissing",
        _ => "BroadcastCaptureFailed"
    };

    // --- the exchange half --------------------------------------------------------

    private Task EstablishOnExchangeAsync(BroadcastRunningSource state) => EstablishOnExchangeCoreAsync(state, CancellationToken.None);

    // SP-0252: the start path passes the person's cancel; the session-online and test paths have none.
    private async Task EstablishOnExchangeCoreAsync(BroadcastRunningSource state, CancellationToken cancel)
    {
        var generation = Volatile.Read(ref _exchangeGeneration);
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
            ReportState(state, _exchange.OwnKeyMissing ? "ExchangeE2eOwnKeyMissing" : ExchangeSealedState.ServerNoE2e.MessageKey());
            return;
        }

        // Item M: from the moment it carries an exchange endpoint the broadcast is protected - for the rest of its life.
        state.Announced = relay || tunnel;
        await ApplyProtectionAsync(state).ConfigureAwait(false);
        var sealing = await PrepareSealingAsync(state, cancel).ConfigureAwait(false);
        if (sealing is null)
        {
            return;
        }
        var accepted = false;
        await sealing.Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var plan = PlanOf(state, publicEndpoint, relay, tunnel, BroadcastSealMode.Record);
            var descriptor = BroadcastDescriptorWriter.Write(plan);
            state.Plan = plan;
            state.DescriptorJson = descriptor;
            state.AnnouncedMaxListeners = Settings.MaxListeners;
            if (SealRecordBox(state, sealing) is null)
            {
                ReportState(state, ExchangeSealedState.E2eMissing.MessageKey());
                return;
            }

            // Constraint 3: the broadcast starts once the server answered `broadcast-started`, with or
            // without a LAN address. A refusal is the user's message, never a stalled start.
            // The person's cancel (SP-0252) ends the wait for the answer as it ends the start: the link makes the one
            // deadline source answer both the timeout and the cancel, and the filters below tell them apart.
            using var startDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            startDeadline.CancelAfter(StartAnswerTimeout);
            var answerTask = _exchange.WaitForBroadcastStartAnswerAsync(state.BroadcastId, StartAnswerTimeout, startDeadline.Token);
            var posted = await PostRecordAsync(state, "broadcast-start", startDeadline.Token).ConfigureAwait(false);
            if (!posted)
            {
                startDeadline.Cancel();
                try { await answerTask.ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
                cancel.ThrowIfCancellationRequested();
                Diagnose(state.Source, "broadcast-start-not-sent");
                ReportState(state, "ExchangeUnavailable");
                return;
            }

            string? answer;
            try
            {
                answer = await answerTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                Diagnose(state.Source, "broadcast-start-timed-out");
                ReportState(state, "ExchangeUnavailable");
                return;
            }

            if (answer is not null)
            {
                Diagnose(state.Source, "broadcast-start-refused", "code=" + (ExchangeSourceService.IsBroadcastRefusal(answer) ? answer : "unknown"));
                ReportState(state, ExchangeProtocol.RefusalKey(answer));
                return;
            }

            lock (_gate)
            {
                if (generation != _exchangeGeneration) return;
                state.ExchangeStarted = true;
                state.AnnouncedServer = SafeEndpoint(_exchange.Account.Host + ":" + _exchange.Account.Port);
                state.RecipientNames = RecipientNames(sealing.Sealer.Box!);
                state.FailureKey = null;
                Diagnose(state.Source, "broadcast-start-accepted", "server=" + state.AnnouncedServer + " | recipients=" + state.RecipientNames.Count);
                accepted = true;
                if (relay && state.CaptureReady)
                {
                    // Every upload replacement, including a reconnect, carries a new epoch (item DL).
                    state.Sealing?.Sealer.Relay?.NewEpoch();
                    StartUpload(state);
                }
            }
        }
        finally
        {
            // S5-5: a start the server did not take must not leave RELAY or TUNNEL endpoints in the copied link, the QR or the Drive record.
            if (!accepted)
            {
                RebuildPlan(state);
            }
            sealing.Gate.Release();
        }
        UpdateRouteVerdict(state);
        await PublishDriveAsync().ConfigureAwait(false);
    }
    /// <summary>The relay upload: a second TLS data stream - `hello`, `upload`, chunks - owned by this task.</summary>
    private void StartUpload(BroadcastRunningSource state)
    {
        lock (state.UploadGate)
        {
            if (state.UploadChunks is not null)
            {
                return;
            }

            var isVideo = !BroadcastSources.IsAudioOnly(state.Source);
            var chunks = Channel.CreateBounded<BroadcastMediaChunk>(isVideo ? VideoUploadQueueChunks : UploadQueueChunks);
            var retire = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.UploadAccepted = false;
            state.UploadJoined = false;
            state.UploadStartupBuffer.Clear();
            state.UploadChunks = chunks;
            state.UploadRetire = retire;
            state.UploadReady = ready;
            state.UploadTask = RunUploadAsync(state, chunks, retire.Token, ready);
        }
    }

    private async Task RunUploadAsync(BroadcastRunningSource state, Channel<BroadcastMediaChunk> chunks, CancellationToken retire,
        TaskCompletionSource ready)
    {
        var account = _exchange.Account;
        var sealing = state.Sealing;
        // S5-1: connect and both handshake reads end on the retire token or this deadline, so a relay that accepts the
        // connection and stays silent cannot hold the task and its TLS stream after the broadcast is stopped. The body
        // below runs on the retire token alone.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(retire);
        handshake.CancelAfter(UploadHandshakeTimeout);
        try
        {
            using var connection = await ExchangeTlsConnection
                .OpenAsync(account.Host, account.Port, account.Pin, handshake.Token, account.PinKey)
                .ConfigureAwait(false);
            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "hello", deviceId = account.DeviceId, deviceToken = account.Token,
                productVersion = ProductInfo.Version, keepaliveSeconds = 30
            }, false, handshake.Token).ConfigureAwait(false);
            using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", handshake.Token).ConfigureAwait(false);
            if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
            {
                Diagnose(state.Source, "relay-upload-refused", "code=" + ExchangeProtocol.RefusalKey(ExchangeProtocol.String(welcome.RootElement, "reason")));
                ReportState(state, "BroadcastRelayLost");
                return;
            }

            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                // Item DL: a sealed upload declares the sealed type; the true media type travels inside the key ring.
                schemaVersion = 2, type = "upload", broadcastId = state.BroadcastId,
                contentType = sealing is null ? MediaTypeOf(state.Source) : ExchangeSealedRelayRecords.ContentType
            }, true, handshake.Token).ConfigureAwait(false);
            using var accepted = await ExchangeHandshake.ReadAsync(connection.Stream, "upload-accepted", handshake.Token).ConfigureAwait(false);
            if (ExchangeProtocol.String(accepted.RootElement, "type") == "refused")
            {
                Diagnose(state.Source, "relay-upload-refused", "code=" + ExchangeProtocol.RefusalKey(ExchangeProtocol.String(accepted.RootElement, "reason")));
                ReportState(state, "BroadcastRelayLost");
                return;
            }

            Diagnose(state.Source, "relay-upload-accepted");
            lock (state.UploadGate)
            {
                state.UploadAccepted = true;
                if (state.UploadStartupBuffer.Count > 0)
                {
                    state.UploadJoined = true;
                    foreach (var buffered in state.UploadStartupBuffer)
                    {
                        chunks.Writer.TryWrite(buffered);
                    }
                    state.UploadStartupBuffer.Clear();
                }
            }

            // A sealed upload always begins with its header chunk (the key ring, then the format header's record when the
            // format has one) and then carries one media record per chunk (item DL). Every audio chunk is whole ADTS frames
            // and joinable (7.6): a listener may start at any one.
            await ExchangeSealedUpload.WriteAsync(connection.Stream, sealing?.Sealer.Relay, state.FormatHeader ?? [],
                chunks.Reader.ReadAllAsync(CancellationToken.None), () => ready.TrySetResult(), retire).ConfigureAwait(false);

            // The clean end of 7.6: a length of zero.
            await ExchangeUploadChunks.WriteEndAsync(connection.Stream, retire).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (retire.IsCancellationRequested)
        {
            // A replacement upload took over (item N): this stream just closes, with no end that would end the broadcast.
        }
        catch (ObjectDisposedException) when (retire.IsCancellationRequested)
        {
            // S13: teardown retired this upload and zeroed the sealer under the pump, which now refuses to seal. Nothing
            // unsealed was written; the stream just closes, like a retired upload, with no end and no relay-lost message.
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // The upload ends; the LAN share keeps serving. The next session online re-establishes the
            // exchange half; no retry runs in the background (constraint 6).
            Diagnose(state.Source, "relay-upload-lost", "type=" + exception.GetType().Name);
            ReportState(state, "BroadcastRelayLost");
        }
        finally
        {
            ready.TrySetResult();
            lock (state.UploadGate)
            {
                state.UploadStartupBuffer.Clear();
            }
        }
    }
    private void OnSessionOnline()
    {
        // Item H: publish again after every welcome - same ids, so the record is replaced, not doubled.
        _ = EstablishRunningAsync();

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

    /// <summary>
    /// S5-2: the session-online establish runs under the same lock as start and stop, and reads _running only once it
    /// holds it, so it cannot re-create the sealing lease, the certificate and an upload for a source Stop or StopAll
    /// just ended, nor race the start path's own establish (a source that start already established is skipped).
    /// </summary>
    private async Task EstablishRunningAsync()
    {
        await _sourceOperations.WaitAsync().ConfigureAwait(false);
        try
        {
            List<BroadcastRunningSource> states;
            lock (_gate)
            {
                states = [.. _running.Values];
            }

            foreach (var state in states)
            {
                if (!state.ExchangeStarted)
                {
                    try { await EstablishOnExchangeAsync(state).ConfigureAwait(false); }
                    catch (Exception exception) when (IsConnectionFailure(exception))
                    {
                        HandlerBoundary.Record("BROADCAST ESTABLISH FAULT | type=" + exception.GetType().Name);
                        Report("BroadcastRelayLost");
                    }
                }
            }
        }
        finally { _sourceOperations.Release(); }
    }

    private void OnSessionOffline()
    {
        lock (_gate)
        {
            _exchangeGeneration++;
            foreach (var state in _running.Values)
            {
                if (state.ExchangeStarted) Diagnose(state.Source, "session-lost");
                state.ExchangeStarted = false;
                UpdateRouteVerdict(state);
                state.UploadRetire?.Cancel();
                state.UploadChunks?.Writer.TryComplete();
                state.UploadChunks = null;
                lock (state.UploadGate)
                {
                    state.UploadStartupBuffer.Clear();
                }
            }
        }
    }

    private void OnLanListenerCount(BroadcastRunningSource state, int lanListeners)
    {
        lock (_gate)
        {
            if (!IsRunning(state) || state.LastLanListeners == lanListeners) return;
            state.LastLanListeners = lanListeners;
        }
        Diagnose(state.Source, "listeners-changed", "lan=" + lanListeners);
        // Item M: the server counts the relay and the tunnels; the producer answers for the LAN share,
        // lowering the figure while LAN listeners are connected.
        if (Settings.MaxListeners <= 0 || !state.ExchangeStarted || state.DescriptorJson is null)
        {
            return;
        }

        state.AnnouncedMaxListeners = Math.Max(0, Settings.MaxListeners - lanListeners);
        _ = PostLanListenerCountAsync(state);
    }

    /// <summary>S5-4: the post owns its timeout source for as long as it is pending, so the 2 s limit can fire; the next count repeats a lost update.</summary>
    private async Task PostLanListenerCountAsync(BroadcastRunningSource state)
    {
        using var timeout = new CancellationTokenSource(EndWriteTimeout);
        // The update repeats the record whole, box included: a record without it would read as unsealed (item DK).
        _ = await PostRecordAsync(state, "broadcast-update", timeout.Token).ConfigureAwait(false);
    }
    private async Task TeardownAsync(BroadcastRunningSource state)
    {
        // SP-0250 requirement 9 / DEVICE-EXCHANGE item DX: `broadcast-end` is written before any stream of the broadcast
        // closes - the capture's chunks, the relay upload, the tunnels and the LAN listener - so a consumer that reads the
        // directory's removal and one that reads a stream's end meet the same broadcast. It is an order, not a request:
        // it has no answer, and with no control stream the write returns at once and holds nothing (a stop is never held
        // for the server). A write that does not land inside EndWriteTimeout is given up, never waited for.
        if (state.ExchangeStarted)
        {
            using var timeout = new CancellationTokenSource(EndWriteTimeout);
            var ended = await _exchange.PostControlEnvelopeAsync(new
            {
                schemaVersion = 2, type = "broadcast-end", broadcastId = state.BroadcastId
            }, timeout.Token).ConfigureAwait(false);
            Diagnose(state.Source, "broadcast-end", "sent=" + ended);
        }

        if (state.Video is not null) await state.Video.DisposeAsync().ConfigureAwait(false);
        await Task.Run(state.Dispose).ConfigureAwait(false);
        // Then the relay upload ends cleanly - the zero-length chunk of 7.6 - and then the LAN listener closes
        // every listener's chunked body and its port.
        state.UploadChunks?.Writer.TryComplete();
        state.UploadChunks = null;
        lock (state.UploadGate)
        {
            state.UploadStartupBuffer.Clear();
        }
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

        // S5-1: whatever is left of the upload (a relay silent in the handshake, a stuck write) ends now, before the sealer is zeroed.
        state.UploadRetire?.Cancel();
        state.Sealing?.Dispose();
        state.Sealing = null;
        if (state.LanServer is not null)
        {
            // Every listener's chunked body ends cleanly.
            await state.LanServer.StopAsync().ConfigureAwait(false);
            Diagnose(state.Source, "listener-unbound", "scope=loopback | port=" + state.LocalPort);
            if (state.LanAddress is not null)
                Diagnose(state.Source, "listener-unbound", "scope=lan | port=" + state.LocalPort);
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

        _ = ExchangeTunnelBridge.RunAsync(_exchange, tunnel, state.LocalPort, cancellationToken, state.Sealing.Sealer.Tunnel,
                () => Diagnose(state.Source, "tunnel-bridge-opened"))
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

    internal static string TitleOf(BroadcastSource source) => source switch
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
