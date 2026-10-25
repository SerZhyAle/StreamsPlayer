using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StreamsPlayer.App;

/// <remarks>
/// Left, Top, Width and Height are where the device context reads this monitor, in the coordinates of the DPI state this
/// process runs under. The size spelled inside <paramref name="Name"/> is the physical one (SP-0244), so on a monitor scaled
/// unlike the system the two differ, and the capture must keep using the rectangle.
/// </remarks>
internal sealed record WindowsMonitor(string Id, string Name, int Left, int Top, int Width, int Height);

/// <summary>
/// Captures the selected whole monitor at bounded resolution. The picture is read through the graphics stack and scaled by
/// the GPU where the monitor allows it (SP-0243); otherwise, and whenever that path becomes unavailable, through the device
/// context with the conversion on the CPU. Every call stays on the encoder worker.
/// </summary>
internal sealed class WindowsScreenCapture : IDisposable
{
    private const int CurrentSettings = -1; // ENUM_CURRENT_SETTINGS
    private readonly WindowsMonitor _monitor;
    private WindowsDesktopDuplication? _duplication;
    private IntPtr _desktop;
    private IntPtr _memory;
    private IntPtr _bitmap;
    private IntPtr _previous;
    private IntPtr _pixels;
    internal int Width { get; }
    internal int Height { get; }

    internal WindowsScreenCapture(WindowsMonitor monitor)
    {
        _monitor = monitor;
        _duplication = WindowsDesktopDuplication.TryOpen(monitor);
        if (_duplication is not null)
        {
            (Width, Height) = (_duplication.Width, _duplication.Height);
            return;
        }
        (Width, Height) = Fit(monitor.Width, monitor.Height);
        OpenDeviceContext();
    }

    /// <summary>The picture is valid until the next call.</summary>
    internal byte[] Read()
    {
        if (_duplication is not null)
        {
            try { return _duplication.Read(); }
            catch (DesktopDuplicationUnavailableException)
            {
                _duplication.Dispose();
                _duplication = null;
                OpenDeviceContext();
            }
        }
        return ReadDeviceContext();
    }

    private void OpenDeviceContext()
    {
        _desktop = GetDC(IntPtr.Zero);
        _memory = CreateCompatibleDC(_desktop);
        var info = new BitmapInfo { Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
        _bitmap = CreateDIBSection(_desktop, ref info, 0, out _pixels, IntPtr.Zero, 0);
        if (_desktop == IntPtr.Zero || _memory == IntPtr.Zero || _bitmap == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception();
        }
        _previous = SelectObject(_memory, _bitmap);
        if (_previous == IntPtr.Zero || _previous == new IntPtr(-1))
        {
            Dispose();
            throw new Win32Exception();
        }
        _ = SetStretchBltMode(_memory, 4);
    }

    private unsafe byte[] ReadDeviceContext()
    {
        if (!StretchBlt(_memory, 0, 0, Width, Height, _desktop, _monitor.Left, _monitor.Top,
            _monitor.Width, _monitor.Height, 0x00cc0020 | 0x40000000)) throw new Win32Exception();
        var output = new byte[Width * Height * 3 / 2];
        var pixels = (byte*)_pixels;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Width + x) * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                output[y * Width + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                if ((x & 1) != 0 || (y & 1) != 0) continue;
                // Average the 2x2 block before chroma subsampling so text edges do not pick one corner's colour.
                var next = offset + Width * 4;
                r = (r + pixels[offset + 6] + pixels[next + 2] + pixels[next + 6] + 2) / 4;
                g = (g + pixels[offset + 5] + pixels[next + 1] + pixels[next + 5] + 2) / 4;
                b = (b + pixels[offset + 4] + pixels[next] + pixels[next + 4] + 2) / 4;
                var uv = Width * Height + y / 2 * Width + x;
                output[uv] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                output[uv + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
        return output;
    }

    internal static (int Width, int Height) Fit(int width, int height)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
        var ratio = Math.Min(1, Math.Min(1280.0 / width, 720.0 / height));
        return (Math.Max(2, (int)(width * ratio) & ~1), Math.Max(2, (int)(height * ratio) & ~1));
    }

    internal static IReadOnlyList<WindowsMonitor> Monitors()
    {
        var result = new List<WindowsMonitor>();
        MonitorCallback callback = (IntPtr handle, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(handle, ref info))
            {
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                var (shownWidth, shownHeight) = PhysicalSize(info.Device) ?? (width, height);
                // The id is the stable Windows device name; the name is what the person and the indicator read.
                result.Add(new(info.Device, LocalizationService.Format("BroadcastMonitorName", result.Count + 1, shownWidth, shownHeight),
                    rect.Left, rect.Top, width, height));
            }
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new Win32Exception();
        return result;
    }

    /// <summary>
    /// The mode the monitor runs, in physical pixels (SP-0244). The rectangle EnumDisplayMonitors returns follows the DPI
    /// state the process started under, so a monitor scaled unlike the system one reads as a virtualized size (1920x1080 as
    /// 3360x1890); the display mode is not virtualized, whatever that state. Null when Windows gives no mode, and the name
    /// then states the rectangle it has: a label that is only approximate is still better than a monitor missing from the list.
    /// </summary>
    private static (int Width, int Height)? PhysicalSize(string device)
    {
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>(), DeviceName = "", FormName = "" };
        return EnumDisplaySettings(device, CurrentSettings, ref mode) && mode.PelsWidth > 0 && mode.PelsHeight > 0
            ? ((int)mode.PelsWidth, (int)mode.PelsHeight)
            : null;
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _duplication = null;
        if (_memory != IntPtr.Zero && _previous != IntPtr.Zero) _ = SelectObject(_memory, _previous);
        if (_bitmap != IntPtr.Zero) _ = DeleteObject(_bitmap);
        if (_memory != IntPtr.Zero) _ = DeleteDC(_memory);
        if (_desktop != IntPtr.Zero) _ = ReleaseDC(IntPtr.Zero, _desktop);
        // S8-7: OpenDeviceContext disposes on failure and the owner disposes again; a second pass must find nothing to release.
        _previous = _bitmap = _memory = _desktop = _pixels = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    /// <summary>DEVMODEW as a display driver fills it; only the pixel size is read, the rest keeps the native layout.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TtOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint Used, Important;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode displayMode);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
