using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>Owns one capture and its encoder on an MTA. No frame queue or native call belongs to the dispatcher.</summary>
internal sealed class WindowsVideoBroadcast : IAsyncDisposable
{
    private readonly WindowsMonitor? _monitor;
    private readonly string? _cameraId;
    private readonly BroadcastMpegTs _mux;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private WindowsCameraCapture? _camera;
    private readonly Thread _thread;
    private bool _launched;
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(5);
    private readonly Stopwatch _clock = new();
    internal long CurrentTime100ns => _clock.Elapsed.Ticks;

    internal event Action<BroadcastMediaChunk>? ChunkReady;
    internal event Action<BroadcastCaptureFailure>? Failed;
    internal byte[] Header => _mux.Header();

    internal WindowsVideoBroadcast(WindowsMonitor? monitor, string? cameraId, bool audio)
    {
        if ((monitor is null) == (cameraId is null)) throw new ArgumentException("Choose exactly one video source.");
        _monitor = monitor;
        _cameraId = cameraId;
        _mux = new(audio);
        _thread = new Thread(Run) { IsBackground = true, Name = "broadcast-video", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    internal async Task StartAsync()
    {
        _launched = true;
        _thread.Start();
        await _started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    internal BroadcastMediaChunk Audio(byte[] adts, long time100ns) => _mux.Audio(adts, time100ns);

    internal static Task<bool> EncoderAvailableAsync() => Task.Run(() =>
    {
        try { using var encoder = new WindowsH264Encoder(1280, 720); return true; }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or DllNotFoundException)
        { return false; }
    });

    private void Run()
    {
        WindowsScreenCapture? screen = null;
        WindowsCameraCapture? camera = null;
        try
        {
            if (_monitor is not null) screen = new(_monitor);
            else
            {
                camera = new(_cameraId!);
                lock (_gate) _camera = camera;
            }
            using var encoder = new WindowsH264Encoder(screen?.Width ?? camera!.Width, screen?.Height ?? camera!.Height);
            var clock = _clock;
            clock.Start();
            var pacer = new FramePacer(10_000_000 / WindowsH264Encoder.FramesPerSecond);
            while (!_stop.IsCancellationRequested)
            {
                var started = clock.Elapsed.Ticks;
                var delay = pacer.Delay(started);
                if (delay > 0)
                {
                    _stop.Token.WaitHandle.WaitOne(TimeSpan.FromTicks(delay));
                    continue;
                }
                var frame = screen is not null ? screen.Read() : camera!.Read();
                if (frame is null) continue;
                // Scheduled from when the frame was started, so the read and the encoding share one interval (SP-0243).
                pacer.Started(started);
                var time = clock.Elapsed.Ticks;
                foreach (var encoded in encoder.Encode(frame, time))
                {
                    ChunkReady?.Invoke(_mux.Video(encoded.Bytes, encoded.Time, encoded.Keyframe));
                    _started.TrySetResult();
                }
            }
        }
        catch (Exception exception) when (IsCaptureFailure(exception))
        {
            if (!_stop.IsCancellationRequested)
            {
                if (!_started.TrySetException(exception)) Failed?.Invoke(Failure(exception));
            }
        }
        finally
        {
            lock (_gate) _camera = null;
            camera?.Dispose();
            screen?.Dispose();
            _started.TrySetCanceled();
            _finished.TrySetResult();
        }
    }

    /// <summary>
    /// Every failure the capture and encoder callees raise (S17-1). The worker and the start-up path in
    /// <c>BroadcastProducerService</c> share this one list, so neither can let an exception reach the unhandled-exception handler.
    /// </summary>
    internal static bool IsCaptureFailure(Exception exception) =>
        exception is COMException or InvalidOperationException or IOException or Win32Exception or ArgumentException
            or UnauthorizedAccessException or InvalidDataException;

    internal static BroadcastCaptureFailure Failure(Exception exception) => exception.HResult switch
    {
        unchecked((int)0x80070005) => BroadcastCaptureFailure.WindowsPermissionDenied,
        unchecked((int)0xc00d3704) => BroadcastCaptureFailure.DeviceInUse,
        _ when exception is UnauthorizedAccessException => BroadcastCaptureFailure.WindowsPermissionDenied,
        _ when exception is InvalidOperationException => BroadcastCaptureFailure.DeviceMissing,
        _ => BroadcastCaptureFailure.Unknown
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_launched)
        {
            await Task.Run(() => { lock (_gate) _camera?.Shutdown(); }).ConfigureAwait(false);
            try { await _finished.Task.WaitAsync(FinishTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // S17-2: the worker is held inside a native call. It finishes its own cleanup when the call returns; this
                // side lets go without waiting, and leaves _stop undisposed because the worker still reads it. The
                // events are cleared so a chunk or failure produced late never reaches an owner already torn down.
                ChunkReady = null;
                Failed = null;
                return;
            }
        }
        _stop.Dispose();
    }
}
