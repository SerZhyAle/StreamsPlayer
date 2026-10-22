using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0229: one open camera, giving BGRA frames until it is disposed. The pictures exist only in memory: they are
/// copied into a buffer the session owns, handed to the caller for the length of one call, and overwritten by the
/// next one. Nothing here writes a frame to disk, sends it, or logs it (and nothing logs a device name or a payload).
/// </summary>
internal sealed class ExchangeCameraSession : IDisposable
{
    /// <summary>Frames handed over at most this often: a QR is aimed by the eye, not read at the camera's frame rate.</summary>
    internal const int MinimumFrameIntervalMilliseconds = 160;

    /// <summary>
    /// Called on a thread-pool thread with a buffer that is valid only until it returns. The caller copies what it
    /// keeps and must not wait for the thread that disposes this session (the call is made inside the session's lock).
    /// </summary>
    internal delegate void FrameHandler(byte[] bgra, int width, int height);

    private readonly object _gate = new();
    private readonly FrameHandler _onFrame;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[] _buffer = [];
    private long _lastFrameAt = long.MinValue / 2;
    private bool _disposed;

    private ExchangeCameraSession(FrameHandler onFrame) => _onFrame = onFrame;

    /// <summary>Opens the camera and starts the frames. Throws what Windows throws; <see cref="ExchangeCameraFailures.Classify"/> reads it.</summary>
    internal static async Task<ExchangeCameraSession> StartAsync(ExchangeCameraInfo camera, FrameHandler onFrame)
    {
        var session = new ExchangeCameraSession(onFrame);
        try
        {
            await session.OpenAsync(camera);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task OpenAsync(ExchangeCameraInfo camera)
    {
        var group = await MediaFrameSourceGroup.FromIdAsync(camera.Id)
            ?? throw new InvalidOperationException("The camera is no longer listed.");
        var capture = new MediaCapture();
        lock (_gate)
        {
            _capture = capture;
        }

        // Exclusive control is what lets the mode be chosen; a camera another program holds then fails here and
        // is reported as one that could not be opened.
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video
        });

        var info = group.SourceInfos.First(ExchangeCameraCatalog.IsColorVideo);
        var source = capture.FrameSources[info.Id];
        var supported = source.SupportedFormats.ToList();
        var chosen = ExchangeCameraFormats.Choose(supported.Select(format => new ExchangeCameraFormat(
            format.Subtype, (int)format.VideoFormat.Width, (int)format.VideoFormat.Height,
            format.FrameRate.Denominator == 0 ? 0 : (double)format.FrameRate.Numerator / format.FrameRate.Denominator)).ToList());
        if (chosen >= 0)
        {
            try
            {
                await source.SetFormatAsync(supported[chosen]);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                // The camera keeps the mode it has; reading may still work, so this is not a failure to open.
            }
        }

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        lock (_gate)
        {
            _reader = reader;
        }

        reader.FrameArrived += OnFrameArrived;
        // A refusal by the privacy settings surfaces as UnauthorizedAccessException from InitializeAsync above; a
        // status other than Success here is a device that is gone or held by another program.
        if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
        {
            throw new InvalidOperationException("The camera did not start.");
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var now = Environment.TickCount64;
        if (_disposed || now - Volatile.Read(ref _lastFrameAt) < MinimumFrameIntervalMilliseconds)
        {
            return;
        }

        SoftwareBitmap? converted = null;
        try
        {
            using var reference = sender.TryAcquireLatestFrame();
            var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null)
            {
                return;
            }

            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                bitmap = converted;
            }

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width < 1 || height < 1 || (long)width * height > ExchangeQrReader.MaximumPixels)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var length = width * height * 4;
                if (_buffer.Length != length)
                {
                    _buffer = new byte[length];
                }

                bitmap.CopyToBuffer(_buffer.AsBuffer());
                Volatile.Write(ref _lastFrameAt, now);
                _onFrame(_buffer, width, height);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException
            or ObjectDisposedException or ArgumentException)
        {
            // One frame that cannot be read is one frame lost; the camera's next frame is the next try.
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>Releases the camera at once. Safe to call twice; no frame is handed over after it returns.</summary>
    public void Dispose()
    {
        MediaFrameReader? reader;
        MediaCapture? capture;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            reader = _reader;
            capture = _capture;
            _reader = null;
            _capture = null;
        }

        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            reader.Dispose();
        }

        capture?.Dispose();
    }
}
