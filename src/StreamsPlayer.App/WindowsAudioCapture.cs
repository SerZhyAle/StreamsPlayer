using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StreamsPlayer.App;

/// <summary>How a capture ended on its own, each with its own localized message (SP-0217 constraint 7).</summary>
internal enum BroadcastCaptureFailure
{
    /// <summary>Windows denied the microphone to desktop apps - the privacy setting, shown in words.</summary>
    WindowsPermissionDenied,

    /// <summary>Another program holds the device exclusively.</summary>
    DeviceInUse,

    /// <summary>The chosen device is gone or has no default to fall back to.</summary>
    DeviceMissing,

    /// <summary>Anything else; the message says the broadcast stopped, not why in HRESULT form.</summary>
    Unknown
}

/// <summary>One capture endpoint of this PC, for the Settings choice.</summary>
internal sealed record WindowsAudioDevice(string Id, string Name);

/// <summary>
/// SP-0217: the microphone and the system sound of this PC, through Windows' own audio session API
/// (WASAPI, no new binary). The microphone is the capture endpoint the user chose, default the Windows
/// default; the system sound is the loopback of the default render endpoint - what the PC is playing.
/// Both are opened shared, with the engine's own converter, at the settings' rate and channel count, so
/// the encoder is fed the format it was configured with. The capture runs on its own thread and never
/// blocks the interface thread (constraint 9).
/// </summary>
/// <remarks>
/// The loopback capture is polled, not event-driven: a loopback stream of a silent render device never
/// signals its event, so a poll is the one shape that works on every machine. Silence is emitted in its
/// place so the broadcast keeps its real-time rate (producer rule 5.1: bytes arrive at the real-time
/// rate, no backlog burst).
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsAudioCapture : IDisposable
{
    private const int PollMilliseconds = 10;
    private const long HundredNanosecondsPerMillisecond = 10_000;
    private const int HresultAccessDenied = unchecked((int)0x80070005);
    private const int AudclntDeviceInUse = unchecked((int)0x8889000A);
    private const int ClsctxInprocServer = 1;
    private const int StgmRead = 0;
    private const int SharedMode = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    private const int StreamFlagsSrcDefaultQuality = 0x08000000;
    private const uint BufferFlagsSilent = 0x2;
    private const int EDataFlowRender = 0;
    private const int EDataFlowCapture = 1;
    private const int ERoleConsole = 0;
    private const uint DeviceStateActive = 0x1;
    private const int VariantTypeString = 31;

    private readonly bool _loopback;
    private readonly string? _deviceId;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Thread _thread;
    private volatile bool _stopping;

    /// <summary>PCM 16-bit frames, on the capture thread; the buffer is valid only for the call.</summary>
    internal event Action<short[]>? PcmReady;

    /// <summary>The capture ended on its own; the broadcast that owns it must stop with this message.</summary>
    internal event Action<BroadcastCaptureFailure>? Failed;

    internal WindowsAudioCapture(bool loopback, string? deviceId, int sampleRate, int channels)
    {
        _loopback = loopback;
        _deviceId = deviceId;
        _sampleRate = sampleRate;
        _channels = channels;
        _thread = new Thread(Run) { IsBackground = true, Name = loopback ? "broadcast-loopback" : "broadcast-microphone" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    internal void Start() => _thread.Start();

    public void Dispose()
    {
        _stopping = true;
        // The capture thread is the only writer of the encoder's input; joining here is what makes the
        // encoder's own disposal race-free in the broadcast teardown.
        if (_thread.IsAlive)
        {
            _thread.Join(2000);
        }
    }

    private void Run()
    {
        IAudioClient? audioClient = null;
        try
        {
            var device = _loopback ? OpenDefaultRenderEndpoint() : OpenCaptureEndpoint(_deviceId);
            if (device is null)
            {
                Failed?.Invoke(BroadcastCaptureFailure.DeviceMissing);
                return;
            }

            try
            {
                var clientInterfaceId = IidAudioClient;
                var activateResult = device.Activate(ref clientInterfaceId, ClsctxInprocServer, IntPtr.Zero,
                    out object clientObject);
                if (activateResult != 0)
                {
                    Failed?.Invoke(FailureOf(activateResult));
                    return;
                }

                audioClient = (IAudioClient)clientObject;
                var format = WAVEFORMATEX.Pcm16(_sampleRate, _channels);
                var bufferDuration = 20 * HundredNanosecondsPerMillisecond;
                var flags = StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality | (_loopback ? StreamFlagsLoopback : 0);
                var initializeResult = audioClient.Initialize(SharedMode, flags, bufferDuration, 0, ref format, IntPtr.Zero);
                if (initializeResult != 0)
                {
                    Failed?.Invoke(FailureOf(initializeResult));
                    return;
                }

                var captureInterfaceId = IidAudioCaptureClient;
                var getServiceResult = audioClient.GetService(ref captureInterfaceId, out object captureObject);
                if (getServiceResult != 0)
                {
                    Failed?.Invoke(FailureOf(getServiceResult));
                    return;
                }

                var capture = (IAudioCaptureClient)captureObject;
                var startResult = audioClient.Start();
                if (startResult != 0)
                {
                    Failed?.Invoke(FailureOf(startResult));
                    return;
                }

                CaptureLoop(capture);
                _ = audioClient.Stop();
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception exception)
        {
            // Nothing of the capture is logged with its ids or addresses; the outcome is the message.
            Failed?.Invoke(exception is COMException com ? FailureOf(com.HResult) : BroadcastCaptureFailure.Unknown);
        }
        finally
        {
            if (audioClient is not null)
            {
                Marshal.ReleaseComObject(audioClient);
            }
        }
    }

    private void CaptureLoop(IAudioCaptureClient capture)
    {
        // One poll slice of silence, so a loopback that has nothing to send keeps the real-time rate.
        var silence = new short[Math.Max(1, _sampleRate * _channels * PollMilliseconds / 1000)];
        while (!_stopping)
        {
            Thread.Sleep(PollMilliseconds);
            var delivered = false;
            var nextPacket = 0u;
            while (capture.GetNextPacketSize(ref nextPacket) == 0 && nextPacket > 0)
            {
                var bufferResult = capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, IntPtr.Zero, IntPtr.Zero);
                if (bufferResult != 0)
                {
                    break;
                }

                if (frames > 0)
                {
                    var samples = new short[frames * _channels];
                    if ((flags & BufferFlagsSilent) != 0)
                    {
                        Array.Clear(samples);
                    }
                    else
                    {
                        Marshal.Copy(data, samples, 0, samples.Length);
                    }

                    PcmReady?.Invoke(samples);
                    delivered = true;
                }

                _ = capture.ReleaseBuffer();
            }

            if (!delivered && _loopback)
            {
                PcmReady?.Invoke(silence);
            }
        }
    }

    private static BroadcastCaptureFailure FailureOf(int hresult) => hresult switch
    {
        HresultAccessDenied => BroadcastCaptureFailure.WindowsPermissionDenied,
        AudclntDeviceInUse => BroadcastCaptureFailure.DeviceInUse,
        _ => BroadcastCaptureFailure.Unknown
    };

    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator)
            ?? throw new InvalidOperationException("The Windows audio device enumerator is unavailable."))!;

    private static IMMDevice? OpenDefaultRenderEndpoint()
    {
        var enumerator = CreateEnumerator();
        try
        {
            return enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out IMMDevice? device) == 0 ? device : null;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static IMMDevice? OpenCaptureEndpoint(string? deviceId)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (deviceId is null)
            {
                return enumerator.GetDefaultAudioEndpoint(EDataFlowCapture, ERoleConsole, out IMMDevice? device) == 0 ? device : null;
            }

            return enumerator.GetDevice(deviceId, out IMMDevice? byId) == 0 ? byId : null;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>The active capture endpoints of this PC, for the Settings choice; never captures.</summary>
    internal static IReadOnlyList<WindowsAudioDevice> ListCaptureDevices()
    {
        var devices = new List<WindowsAudioDevice>();
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.EnumAudioEndpoints(EDataFlowCapture, DeviceStateActive, out IMMDeviceCollection? collection) != 0
                || collection is null || collection.GetCount(out uint count) != 0)
            {
                return devices;
            }

            for (uint index = 0; index < count; index++)
            {
                if (collection.Item(index, out IMMDevice? device) != 0 || device is null)
                {
                    continue;
                }

                try
                {
                    if (device.GetId(out IntPtr idPointer) == 0)
                    {
                        devices.Add(new WindowsAudioDevice(
                            Marshal.PtrToStringUni(idPointer) ?? "",
                            ReadFriendlyName(device)));
                        Marshal.FreeCoTaskMem(idPointer);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }

            return devices;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out IPropertyStore? store) != 0 || store is null)
        {
            return "";
        }

        try
        {
            var key = PROPERTYKEY.DeviceFriendlyName;
            PropVariant value = default;
            if (store.GetValue(ref key, ref value) != 0)
            {
                return "";
            }

            try
            {
                return value.VariantType == VariantTypeString && value.Pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.Pointer) ?? ""
                    : "";
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FormatId;
        public int PropertyId;

        public static PROPERTYKEY DeviceFriendlyName => new()
        {
            // PKEY_Device_FriendlyName of functiondiscoverykeys_devpkey.h, the one key this reads.
            FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            PropertyId = 14
        };
    }

    // The PROPVARIANT a property store hands back, wide enough for the pointer of VT_LPWSTR on x64; the
    // rest of the union is never read, only cleared.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;

        public static WAVEFORMATEX Pcm16(int sampleRate, int channels)
        {
            var blockAlign = (ushort)(channels * 2);
            return new WAVEFORMATEX
            {
                FormatTag = 1, // WAVE_FORMAT_PCM
                Channels = (ushort)channels,
                SamplesPerSecond = (uint)sampleRate,
                BlockAlign = blockAlign,
                BitsPerSample = 16,
                AverageBytesPerSecond = (uint)(sampleRate * blockAlign),
                ExtraSize = 0
            };
        }
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, int classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object deviceObject);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? propertyStore);
        [PreserveSig] int GetId(out IntPtr idPointer);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, ref PropVariant value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, IntPtr devicePosition, IntPtr queuePosition);
        [PreserveSig] int ReleaseBuffer();
        [PreserveSig] int GetNextPacketSize(ref uint frames);
    }
}
