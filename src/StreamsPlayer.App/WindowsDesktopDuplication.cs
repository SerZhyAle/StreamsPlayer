using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamsPlayer.App;

/// <summary>This monitor cannot be read through the graphics stack; the caller falls back to the device context.</summary>
internal sealed class DesktopDuplicationUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// SP-0243: one monitor read through DXGI desktop duplication, the picture scaled and converted to NV12 by the GPU's
/// video processor. Reading a 4K desktop through the device context costs 66 ms, a multiple of the display refresh;
/// this path costs about one millisecond of the worker's time, and the readback is the already scaled picture. Every
/// call belongs to the capture worker: the device, its context and the duplication are never shared.
/// </summary>
internal sealed class WindowsDesktopDuplication : IDisposable
{
    // Long enough to catch the update that lands just after the poll, short enough to leave the frame interval to the encoder.
    private const uint AcquireTimeoutMs = 16;
    private const int PrimeMilliseconds = 1500;
    private const int RebuildBackoffMilliseconds = 250;
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    private readonly string _deviceName;
    private readonly byte[] _frame;
    private Pipeline? _pipeline;
    private long _rebuildAt;
    private bool _hasPicture;
    internal int Width { get; }
    internal int Height { get; }

    private WindowsDesktopDuplication(string deviceName)
    {
        _deviceName = deviceName;
        _pipeline = new Pipeline(deviceName, null);
        (Width, Height) = (_pipeline.Width, _pipeline.Height);
        _frame = new byte[Width * Height * 3 / 2];
    }

    /// <summary>Null when this monitor cannot be duplicated or shows nothing; the device-context path then serves it.</summary>
    internal static WindowsDesktopDuplication? TryOpen(WindowsMonitor monitor)
    {
        WindowsDesktopDuplication? duplication = null;
        try
        {
            duplication = new WindowsDesktopDuplication(monitor.Id);
            if (duplication.Prime()) return duplication;
        }
        catch (Exception exception) when (exception is DesktopDuplicationUnavailableException || GraphicsFailure(exception))
        {
        }
        duplication?.Dispose();
        return null;
    }

    /// <summary>What a missing or refusing graphics stack throws; none of it is caught by the broadcast loop, so none may escape.</summary>
    private static bool GraphicsFailure(Exception exception) =>
        exception is SharpGenException or InvalidCastException or COMException or DllNotFoundException or Win32Exception;

    /// <summary>
    /// The latest picture of the monitor, valid until the next call. A desktop that did not change, a pointer-only update
    /// and a duplication that is being re-established all return the previous picture, which is what a still screen is.
    /// </summary>
    internal byte[] Read()
    {
        if (_pipeline is null && !Rebuild()) return _frame;
        try
        {
            if (!Pull()) Drop();
        }
        catch (SharpGenException exception) when (Transient(exception.ResultCode.Code))
        {
            Drop();
        }
        catch (Exception exception) when (GraphicsFailure(exception))
        {
            throw new DesktopDuplicationUnavailableException("The duplication failed.", exception);
        }
        return _frame;
    }

    private bool Prime()
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * PrimeMilliseconds / 1000;
        while (!_hasPicture && Stopwatch.GetTimestamp() < deadline)
        {
            if (!Pull()) return false;
        }
        return _hasPicture;
    }

    /// <summary>False when the duplication no longer matches the monitor (its mode changed) and must be built again.</summary>
    private bool Pull()
    {
        var pipeline = _pipeline!;
        var result = pipeline.Duplication.AcquireNextFrame(AcquireTimeoutMs, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return true;
        result.CheckError();
        try
        {
            // The first frame, and a pointer-only one, carry no new desktop image; the first one is black.
            if (info.LastPresentTime == 0) return true;
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            if (!pipeline.Scale(texture, _frame)) return false;
            _hasPicture = true;
            return true;
        }
        finally
        {
            pipeline.Duplication.ReleaseFrame();
            resource.Dispose();
        }
    }

    private bool Rebuild()
    {
        if (Stopwatch.GetTimestamp() < _rebuildAt) return false;
        try
        {
            _pipeline = new Pipeline(_deviceName, (Width, Height));
            return true;
        }
        catch (SharpGenException exception) when (Transient(exception.ResultCode.Code))
        {
            Drop();
            return false;
        }
        catch (Exception exception) when (GraphicsFailure(exception))
        {
            throw new DesktopDuplicationUnavailableException("The monitor can no longer be duplicated.", exception);
        }
    }

    private void Drop()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        _rebuildAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * RebuildBackoffMilliseconds / 1000;
    }

    /// <summary>A secure desktop, a locked session, a mode change and a driver reset all end a duplication and pass on their own.</summary>
    private static bool Transient(int code) => code == AccessDeniedHResult
        || code == Vortice.DXGI.ResultCode.AccessLost.Code || code == Vortice.DXGI.ResultCode.AccessDenied.Code
        || code == Vortice.DXGI.ResultCode.NotCurrentlyAvailable.Code || code == Vortice.DXGI.ResultCode.SessionDisconnected.Code
        || code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || code == Vortice.DXGI.ResultCode.DeviceReset.Code
        || code == Vortice.DXGI.ResultCode.NotFound.Code;

    public void Dispose()
    {
        _pipeline?.Dispose();
        _pipeline = null;
    }

    /// <summary>The device, the duplication and the video processor of one monitor at one mode; rebuilt as a whole.</summary>
    private sealed class Pipeline : IDisposable
    {
        private readonly List<IDisposable> _owned = [];
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11VideoContext _videoContext;
        private readonly ID3D11VideoProcessor _processor;
        private readonly ID3D11VideoProcessorInputView _input;
        private readonly ID3D11VideoProcessorOutputView _output;
        private readonly ID3D11Texture2D _desktop;
        private readonly ID3D11Texture2D _scaled;
        private readonly ID3D11Texture2D _staging;
        internal IDXGIOutputDuplication Duplication { get; }
        internal int SourceWidth { get; }
        internal int SourceHeight { get; }
        internal int Width { get; }
        internal int Height { get; }

        /// <param name="target">The size the encoder was built for; null on the first build, which derives it from the monitor.</param>
        internal Pipeline(string deviceName, (int Width, int Height)? target)
        {
            try
            {
                var (adapter, output) = FindOutput(deviceName);
                var device = Create(adapter, out _context);
                var output1 = Own(output.QueryInterface<IDXGIOutput1>());
                Duplication = Own(output1.DuplicateOutput(device));
                var description = Duplication.Description;
                // A rotated or HDR desktop arrives in a layout this path does not convert; the device context handles both.
                if (description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified)
                    || description.ModeDescription.Format != Format.B8G8R8A8_UNorm)
                    throw new DesktopDuplicationUnavailableException("The desktop is rotated or not 8-bit colour.");
                (SourceWidth, SourceHeight) = ((int)description.ModeDescription.Width, (int)description.ModeDescription.Height);
                (Width, Height) = target ?? WindowsScreenCapture.Fit(SourceWidth, SourceHeight);

                var videoDevice = Own(device.QueryInterface<ID3D11VideoDevice>());
                _videoContext = Own(_context.QueryInterface<ID3D11VideoContext>());
                var enumerator = Own(videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive, Usage = VideoUsage.PlaybackNormal,
                    InputWidth = (uint)SourceWidth, InputHeight = (uint)SourceHeight,
                    OutputWidth = (uint)Width, OutputHeight = (uint)Height
                }));
                _processor = Own(videoDevice.CreateVideoProcessor(enumerator, 0));
                _desktop = Own(device.CreateTexture2D(Texture(SourceWidth, SourceHeight, Format.B8G8R8A8_UNorm,
                    ResourceUsage.Default, BindFlags.ShaderResource | BindFlags.RenderTarget, CpuAccessFlags.None)));
                _scaled = Own(device.CreateTexture2D(Texture(Width, Height, Format.NV12,
                    ResourceUsage.Default, BindFlags.RenderTarget, CpuAccessFlags.None)));
                _staging = Own(device.CreateTexture2D(Texture(Width, Height, Format.NV12,
                    ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read)));
                _input = Own(videoDevice.CreateVideoProcessorInputView(_desktop, enumerator, new VideoProcessorInputViewDescription
                {
                    FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new() { MipSlice = 0, ArraySlice = 0 }
                }));
                _output = Own(videoDevice.CreateVideoProcessorOutputView(_scaled, enumerator, new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new() { MipSlice = 0 }
                }));
                _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
                _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, SourceWidth, SourceHeight));
                _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new RawRect(0, 0, Width, Height));
                _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, Width, Height));
                // The driver's own enhancement of a desktop would only blur text.
                _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
                // The desktop is full-range RGB and the encoder has always been fed BT.601 at 16..235; without this the driver
                // chooses BT.709 full range and every colour of the broadcast shifts.
                _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0,
                    new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 0, YCbCr_xvYCC = 0, Nominal_Range = 2 });
                _videoContext.VideoProcessorSetOutputColorSpace(_processor,
                    new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 0, YCbCr_xvYCC = 0, Nominal_Range = 1 });
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Scales the acquired desktop image to the encoder's size and copies it out as NV12; false when its size is not this mode's.</summary>
        internal unsafe bool Scale(ID3D11Texture2D desktop, byte[] frame)
        {
            var size = desktop.Description;
            if (size.Width != SourceWidth || size.Height != SourceHeight) return false;
            _context.CopyResource(_desktop, desktop);
            _videoContext.VideoProcessorBlt(_processor, _output, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = _input }]);
            _context.CopyResource(_staging, _scaled);
            var map = _context.Map(_staging, 0, MapMode.Read);
            try
            {
                var source = (byte*)map.DataPointer;
                var pitch = (int)map.RowPitch;
                fixed (byte* target = frame)
                {
                    // The luma rows, then the interleaved chroma rows that follow them in the mapped surface.
                    for (var y = 0; y < Height; y++) Buffer.MemoryCopy(source + (long)y * pitch, target + y * Width, Width, Width);
                    for (var y = 0; y < Height / 2; y++)
                        Buffer.MemoryCopy(source + (long)(Height + y) * pitch, target + Width * Height + y * Width, Width, Width);
                }
            }
            finally { _context.Unmap(_staging, 0); }
            return true;
        }

        private static Texture2DDescription Texture(int width, int height, Format format, ResourceUsage usage, BindFlags bind, CpuAccessFlags cpu) => new()
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new(1, 0), Usage = usage, BindFlags = bind, CPUAccessFlags = cpu
        };

        private ID3D11Device Create(IDXGIAdapter1 adapter, out ID3D11DeviceContext context)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out context).CheckError();
            Own(context);
            return Own(device);
        }

        /// <summary>The output is matched by the Windows device name the monitor list carries, on the adapter that owns it.</summary>
        private (IDXGIAdapter1 Adapter, IDXGIOutput Output) FindOutput(string deviceName)
        {
            var factory = Own(DXGI.CreateDXGIFactory1<IDXGIFactory1>());
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                Own(adapter);
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    Own(output);
                    if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return (adapter, output);
                }
            }
            throw new SharpGenException(Vortice.DXGI.ResultCode.NotFound);
        }

        private T Own<T>(T item) where T : IDisposable
        {
            _owned.Add(item);
            return item;
        }

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear();
        }
    }
}
