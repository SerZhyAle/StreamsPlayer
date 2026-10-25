using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class BroadcastProducerService
{
    internal bool IsGuestOnly => _exchange.Account.Guest
        || _exchange.Account.Token is null && _exchange.Guests.Entries.Count > 0;

    internal string SourceTitle(BroadcastSource source)
    {
        lock (_gate) return _running.TryGetValue(source, out var state) ? state.Title : TitleOf(source);
    }

    private Task<bool>? _videoAvailable;

    /// <summary>Whether Windows' H.264 encoder starts on this PC, asked once; where it does not, the screen and the camera are not offered (requirement 2).</summary>
    internal Task<bool> VideoAvailableAsync() => _videoAvailable ??= WindowsVideoBroadcast.EncoderAvailableAsync();

    // Each source keeps the contract's fixed paths on its own deterministic port, with no fallback on conflict.
    private int SourcePort(BroadcastSource source)
    {
        var port = Settings.LanPort + (int)source;
        return port is >= 1 and <= 65535 ? port : 0;
    }

    private async Task<bool> StartVideoAsync(BroadcastSource source, string? lanAddress)
    {
        var settings = Settings;
        var port = SourcePort(source);
        if (port == 0) { Report("BroadcastPortTaken"); return false; }
        var choices = await Task.Run(() => source == BroadcastSource.Screen
            ? (Monitors: WindowsScreenCapture.Monitors(), Cameras: (IReadOnlyList<WindowsCameraDevice>)[])
            : (Monitors: (IReadOnlyList<WindowsMonitor>)[], Cameras: WindowsCameraCapture.Devices())).ConfigureAwait(false);
        var monitor = settings.MonitorDeviceId is null ? choices.Monitors.FirstOrDefault()
            : choices.Monitors.FirstOrDefault(item => item.Id == settings.MonitorDeviceId);
        var camera = settings.CameraDeviceId is null ? choices.Cameras.FirstOrDefault()
            : choices.Cameras.FirstOrDefault(item => item.Id == settings.CameraDeviceId);
        if (monitor is null && camera is null) { Report("BroadcastDeviceMissing"); return false; }
        var companionName = source == BroadcastSource.Screen ? settings.ScreenAudioSource : settings.CameraAudioSource;
        BroadcastSource? companion = companionName switch
        {
            "mic" => BroadcastSource.Microphone, "system-audio" => BroadcastSource.SystemSound, _ => null
        };
        var channels = companion == BroadcastSource.SystemSound ? settings.SystemSoundChannels : settings.MicrophoneChannels;
        WindowsAacEncoder? encoder = null;
        try
        {
            if (companion is not null)
                encoder = await Task.Run(() => new WindowsAacEncoder(settings.SampleRate, channels, settings.BitrateKbps)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { Report("BroadcastEncoderMissing"); return false; }
        var video = new WindowsVideoBroadcast(monitor, camera?.Id, companion is not null);
        var token = ExchangeProtocol.NewDeviceId();
        var title = LocalizationService.Format("BroadcastSourceDevice", TitleOf(source), monitor?.Name ?? camera!.Name);
        var lan = new BroadcastLanServer(new(title, settings.BitrateKbps, settings.MaxListeners,
            [BroadcastDescriptorWriter.LanPath(source, token)], BroadcastDescriptorWriter.VideoContentType, true));
        try { lan.Start(lanAddress, port); }
        catch (SocketException)
        {
            await lan.StopAsync().ConfigureAwait(false);
            await video.DisposeAsync().ConfigureAwait(false);
            encoder?.Dispose();
            Report("BroadcastPortTaken");
            return false;
        }
        var state = new BroadcastRunningSource
        {
            Source = source, BroadcastId = ExchangeProtocol.NewDeviceId(), Title = title, StartedAt = DateTimeOffset.Now,
            Encoder = encoder, Video = video, OpaqueToken = token, AudioSource = companion,
            Capture = companion is null ? null : new WindowsAudioCapture(companion == BroadcastSource.SystemSound,
                companion == BroadcastSource.Microphone ? settings.MicrophoneDeviceId : null, settings.SampleRate, channels),
            Frames = new AdtsFrameStream(), SampleRate = settings.SampleRate, Channels = channels, AudioBitrate = settings.BitrateKbps,
            LanServer = lan, LanAddress = lanAddress, LocalPort = port, FormatHeader = video.Header
        };
        state.LinkPair = NewLinkPair(out var generatedLink);
        state.LinkIsGenerated = generatedLink;
        await ApplyProtectionAsync(state).ConfigureAwait(false);
        state.Plan = PlanOf(state, null, false, false, BroadcastSealMode.None);
        state.DescriptorJson = BroadcastDescriptorWriter.Write(state.Plan);
        video.ChunkReady += chunk => OnVideoChunk(state, chunk);
        video.Failed += failure => OnCaptureFailed(state, failure);
        if (state.Capture is { } capture)
        {
            capture.PcmReady += pcm => OnPcm(state, pcm);
            capture.Failed += failure => OnCaptureFailed(state, failure);
        }
        lan.LanListenerCountChanged += count => OnLanListenerCount(state, count);
        lock (_gate) _running[source] = state;
        Changed?.Invoke();
        if (!Settings.Allowed || IsGuestOnly)
        {
            await StopCoreAsync(source).ConfigureAwait(false);
            return false;
        }
        // The device is opened before anything is announced: a refused Windows permission or a device another program
        // holds then ends here, with a message, and the account's directory never lists a broadcast with no picture.
        try { await video.StartAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or IOException
            or Win32Exception or ArgumentException or TimeoutException or OperationCanceledException)
        {
            await StopCoreAsync(source).ConfigureAwait(false);
            Report(CaptureFailureKey(WindowsVideoBroadcast.Failure(exception)));
            return false;
        }
        state.CaptureReady = true;
        state.AudioTime100ns = video.CurrentTime100ns;
        state.Capture?.Start();
        await EstablishOnExchangeAsync(state).ConfigureAwait(false);
        if (lanAddress is null && !state.ExchangeStarted)
        {
            await StopCoreAsync(source).ConfigureAwait(false);
            return false;
        }
        await SaveChosenAsync(source, true).ConfigureAwait(false);
        await PublishDriveAsync().ConfigureAwait(false);
        Changed?.Invoke();
        return true;
    }

    private void OnVideoChunk(BroadcastRunningSource state, BroadcastMediaChunk chunk)
    {
        // Both paths receive the very same MPEG-TS bytes and the same IDR decision.
        state.LanServer?.Enqueue(chunk.Payload, chunk.Joinable);
        PostUploadChunk(state, chunk);
    }

    private async Task ReconfigureLanAsync()
    {
        var address = Settings.LanListening ? ChooseLanAddress() : null;
        List<BroadcastRunningSource> states;
        lock (_gate) states = [.. _running.Values];
        foreach (var state in states)
        {
            try { state.LanServer?.SetLanAddress(address); state.LanAddress = address; }
            catch (SocketException) { state.LanAddress = null; Report("BroadcastPortTaken"); }
            if (state.LanAddress is null && !state.ExchangeStarted)
            {
                await StopCoreAsync(state.Source).ConfigureAwait(false);
                continue;
            }
            RebuildPlan(state);
            if (state.ExchangeStarted)
            {
                using var timeout = new CancellationTokenSource(EndWriteTimeout);
                await PostRecordAsync(state, "broadcast-update", timeout.Token).ConfigureAwait(false);
            }
        }
        await PublishDriveAsync().ConfigureAwait(false);
    }
}
