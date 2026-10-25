using System.Runtime.InteropServices;
using System.IO;
using static StreamsPlayer.App.MediaFoundationNative;
using static StreamsPlayer.App.WindowsH264Encoder;

namespace StreamsPlayer.App;

internal sealed record WindowsCameraDevice(string Id, string Name);

/// <summary>The source reader owns conversion to bounded NV12; no camera pixels leave this worker except through its encoder.</summary>
internal sealed class WindowsCameraCapture : IDisposable
{
    private const uint VideoStream = 0xfffffffc;
    private static readonly Guid SourceKind = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    private static readonly Guid VideoKind = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid DeviceId = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    private static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    private readonly IMFSourceReader _reader;
    private readonly IMFMediaSource _source;
    private int _shutdown;
    internal int Width { get; }
    internal int Height { get; }

    internal WindowsCameraCapture(string id)
    {
        Check(MFStartup(0x20070, 0));
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            foreach (var activation in Activations())
            {
                try
                {
                    if (source is not null || Text(activation, DeviceId) != id) continue;
                    var iid = typeof(IMFMediaSource).GUID;
                    Check(activation.ActivateObject(ref iid, out var instance));
                    source = (IMFMediaSource)instance;
                }
                finally { Marshal.ReleaseComObject(activation); }
            }
            if (source is null) throw new InvalidOperationException("The selected camera is unavailable.");
            Check(MFCreateAttributes(out var attributes, 1));
            try
            {
                var processing = new Guid("0f81da2c-b537-4672-a8b2-a681b17307a3");
                Check(attributes!.SetUINT32(ref processing, 1));
                Check(MFCreateSourceReaderFromMediaSource(source, attributes, out reader));
            }
            finally { if (attributes is not null) Marshal.ReleaseComObject(attributes); }
            Check(reader!.GetCurrentMediaType(VideoStream, out var native));
            try
            {
                var size = FrameSize;
                Check(native!.GetUINT64(ref size, out var dimensions));
                (Width, Height) = WindowsScreenCapture.Fit((int)(dimensions >> 32), (int)(dimensions & uint.MaxValue));
            }
            finally { if (native is not null) Marshal.ReleaseComObject(native); }
            var type = Type(Nv12, Width, Height);
            try
            {
                var rate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
                Check(type.DeleteItem(ref rate));
                Check(reader.SetCurrentMediaType(VideoStream, IntPtr.Zero, type));
            }
            finally { Marshal.ReleaseComObject(type); }
            _source = source;
            _reader = reader;
        }
        catch
        {
            if (reader is not null) Marshal.ReleaseComObject(reader);
            if (source is not null) { source.Shutdown(); Marshal.ReleaseComObject(source); }
            MFShutdown();
            throw;
        }
    }

    internal byte[]? Read()
    {
        Check(_reader.ReadSample(VideoStream, 0, out _, out var flags, out _, out var sample));
        if ((flags & 3) != 0) throw new InvalidOperationException("The camera stream ended.");
        if (sample is null) return null;
        try
        {
            Check(sample.ConvertToContiguousBuffer(out var buffer));
            try
            {
                Check(buffer!.Lock(out var data, out _, out var length));
                try
                {
                    // S8-4: IOException, not InvalidDataException (a SystemException the worker's catch did not list).
                    if (length != Width * Height * 3 / 2) throw new IOException("Unexpected camera frame layout.");
                    var pixels = new byte[length];
                    Marshal.Copy(data, pixels, 0, length);
                    return pixels;
                }
                finally { Check(buffer.Unlock()); }
            }
            finally { if (buffer is not null) Marshal.ReleaseComObject(buffer); }
        }
        finally { Marshal.ReleaseComObject(sample); }
    }

    /// <summary>Called from an MTA on stop to release a blocked synchronous read before joining its worker.</summary>
    internal void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 0) _ = _source.Shutdown();
    }

    public void Dispose()
    {
        Shutdown();
        Marshal.ReleaseComObject(_reader);
        Marshal.ReleaseComObject(_source);
        MFShutdown();
    }

    internal static IReadOnlyList<WindowsCameraDevice> Devices()
    {
        Check(MFStartup(0x20070, 0));
        try
        {
            var devices = new List<WindowsCameraDevice>();
            foreach (var activation in Activations())
            {
                try { devices.Add(new(Text(activation, DeviceId), Text(activation, FriendlyName))); }
                finally { Marshal.ReleaseComObject(activation); }
            }
            return devices;
        }
        finally { MFShutdown(); }
    }

    private static IReadOnlyList<IMFActivate> Activations()
    {
        Check(MFCreateAttributes(out var attributes, 1));
        try
        {
            var key = SourceKind;
            var kind = VideoKind;
            Check(attributes!.SetGUID(ref key, ref kind));
            Check(MFEnumDeviceSources(attributes, out var pointers, out var count));
            try
            {
                var result = new List<IMFActivate>();
                for (var index = 0; index < count; index++)
                {
                    var pointer = Marshal.ReadIntPtr(pointers, index * IntPtr.Size);
                    try { result.Add((IMFActivate)Marshal.GetObjectForIUnknown(pointer)); }
                    finally { Marshal.Release(pointer); }
                }
                return result;
            }
            finally { Marshal.FreeCoTaskMem(pointers); }
        }
        finally { if (attributes is not null) Marshal.ReleaseComObject(attributes); }
    }

    private static string Text(IMFActivate activation, Guid key)
    {
        Check(activation.GetAllocatedString(ref key, out var text, out _));
        try { return Marshal.PtrToStringUni(text) ?? ""; }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IMFAttributes? attributes, uint initialSize);
    [DllImport("mf.dll")]
    private static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr activations, out uint count);
    [DllImport("mfreadwrite.dll")]
    private static extern int MFCreateSourceReaderFromMediaSource(IMFMediaSource source, IMFAttributes attributes, out IMFSourceReader? reader);

    [ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaSource
    {
        [PreserveSig] int GetEvent(uint flags, out IntPtr mediaEvent);
        [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
        [PreserveSig] int EndGetEvent(IntPtr result, out IntPtr mediaEvent);
        [PreserveSig] int QueueEvent(uint type, ref Guid extended, int status, IntPtr value);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
        [PreserveSig] int CreatePresentationDescriptor(out IntPtr descriptor);
        [PreserveSig] int Start(IntPtr descriptor, ref Guid timeFormat, IntPtr position);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Shutdown();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType? type);
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType? type);
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, IntPtr position);
        [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
        [PreserveSig] int Flush(uint stream);
    }
}
