using System.Runtime.InteropServices;
using static StreamsPlayer.App.MediaFoundationNative;

namespace StreamsPlayer.App;

/// <summary>The Windows inbox software MFT. Its COM lifetime and every call belong to the feeding MTA worker.</summary>
internal sealed class WindowsH264Encoder : IDisposable
{
    internal const int FramesPerSecond = 30;
    internal const int Bitrate = 2_000_000;
    private readonly IMFTransform _transform;
    private readonly ICodecApi _codec;
    private readonly int _frameBytes;
    private long _lastForced = -20_000_000;
    private bool _disposed;
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    internal WindowsH264Encoder(int width, int height)
    {
        Check(MFStartup(0x20070, 0));
        var clsid = new Guid("6ca50344-051a-4ded-9779-a43305165e35");
        var iid = typeof(IMFTransform).GUID;
        object? instance = null;
        try
        {
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out instance));
            _transform = (IMFTransform)instance!;
            _codec = (ICodecApi)instance!;
            SetCodec("95f31b26-95a4-41aa-9303-246a7fc6eef1", (uint)60);
            SetCodec("b0c8bf60-16f7-4951-a30b-1db1609293d6", (uint)2);
            SetCodec("9c27891a-ed7a-40e1-88e8-b22727a024ee", true);
            var output = Type(new("34363248-0000-0010-8000-00aa00389b71"), width, height);
            try
            {
                Set(output, "20332624-fb0d-4d9e-bd0d-cbf6786c102e", Bitrate);
                Set(output, "ad76a80b-2d5c-4e0b-b375-64e520137036", 66);
                Check(_transform.SetOutputType(0, output, 0));
            }
            finally { Marshal.ReleaseComObject(output); }
            var input = Type(Nv12, width, height);
            try { Check(_transform.SetInputType(0, input, 0)); }
            finally { Marshal.ReleaseComObject(input); }
            Check(_transform.ProcessMessage(unchecked((int)0x10000000), IntPtr.Zero));
            Check(_transform.ProcessMessage(unchecked((int)0x10000003), IntPtr.Zero));
            _frameBytes = checked(width * height * 3 / 2);
        }
        catch
        {
            if (instance is not null) Marshal.ReleaseComObject(instance);
            MFShutdown();
            throw;
        }
    }

    internal IReadOnlyList<(byte[] Bytes, long Time, bool Keyframe)> Encode(byte[] nv12, long time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nv12.Length != _frameBytes) throw new ArgumentException("Unexpected video frame size.", nameof(nv12));
        if (time - _lastForced >= 18_000_000)
        {
            SetCodec("398c1b98-8353-475a-9ef2-8f265d260345", (uint)1);
            _lastForced = time;
        }
        var sample = Sample(nv12);
        try
        {
            Check(sample.SetSampleTime(time));
            Check(sample.SetSampleDuration(10_000_000 / FramesPerSecond));
            Check(_transform.ProcessInput(0, sample, 0));
        }
        finally { Marshal.ReleaseComObject(sample); }
        var result = new List<(byte[], long, bool)>();
        while (true)
        {
            Check(_transform.GetOutputStreamInfo(0, out var outputInfo));
            var outputSample = Sample(new byte[Math.Max(_frameBytes, checked((int)outputInfo.Size))]);
            var output = new MftOutputDataBuffer { Sample = outputSample };
            try
            {
                var hr = _transform.ProcessOutput(0, 1, ref output, out _);
                if (hr == unchecked((int)0xc00d6d72)) break;
                Check(hr);
                Check(outputSample.GetSampleTime(out var timestamp));
                var key = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
                _ = outputSample.GetUINT32(ref key, out var clean);
                Check(outputSample.ConvertToContiguousBuffer(out var buffer));
                try
                {
                    Check(buffer!.Lock(out var data, out _, out var length));
                    var bytes = new byte[length];
                    try { Marshal.Copy(data, bytes, 0, length); }
                    finally { Check(buffer.Unlock()); }
                    result.Add((bytes, timestamp, clean != 0));
                }
                finally { if (buffer is not null) Marshal.ReleaseComObject(buffer); }
            }
            finally
            {
                if (output.Events != IntPtr.Zero) Marshal.Release(output.Events);
                Marshal.ReleaseComObject(outputSample);
            }
        }
        return result;
    }

    internal static IMFMediaType Type(Guid subtype, int width, int height)
    {
        Check(MFCreateMediaType(out var type));
        try
        {
            SetGuid(type!, "48eba18e-f8c9-4687-bf11-0a74c9f96a8f", new("73646976-0000-0010-8000-00aa00389b71"));
            SetGuid(type!, "f7e34c9a-42e8-4714-b74b-cb29d72c35e5", subtype);
            var size = FrameSize;
            Check(type!.SetUINT64(ref size, ((ulong)width << 32) | (uint)height));
            var rate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
            Check(type.SetUINT64(ref rate, ((ulong)FramesPerSecond << 32) | 1));
            var aspect = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
            Check(type.SetUINT64(ref aspect, (1ul << 32) | 1));
            Set(type, "e2724bb8-e676-4806-b4b2-a8d6efb44ccd", 2);
            return type;
        }
        catch { if (type is not null) Marshal.ReleaseComObject(type); throw; }
    }

    internal static void Set(IMFMediaType type, string name, uint value)
    {
        var key = new Guid(name);
        Check(type.SetUINT32(ref key, value));
    }

    internal static void SetGuid(IMFMediaType type, string name, Guid value)
    {
        var key = new Guid(name);
        Check(type.SetGUID(ref key, ref value));
    }

    // S8-5: always a COMException carrying the raw HRESULT. Marshal.ThrowExceptionForHR turned E_ACCESSDENIED into an
    // UnauthorizedAccessException (and others into ArgumentException and the like), which the capture catches did not
    // list, so a refused permission escaped the worker instead of reaching Failure() and its permission message.
    internal static void Check(int result)
    {
        if (result < 0) throw new COMException("A Media Foundation call failed.", result);
    }

    private void SetCodec(string name, object value)
    {
        var key = new Guid(name);
        Check(_codec.SetValue(ref key, ref value));
    }

    private static IMFSample Sample(byte[] bytes)
    {
        Check(MFCreateSample(out var sample));
        try
        {
            Check(MFCreateMemoryBuffer(bytes.Length, out var buffer));
            try
            {
                Check(buffer!.Lock(out var data, out _, out _));
                try { Marshal.Copy(bytes, 0, data, bytes.Length); }
                finally { Check(buffer.Unlock()); }
                Check(buffer.SetCurrentLength(bytes.Length));
                Check(sample!.AddBuffer(buffer));
            }
            finally { if (buffer is not null) Marshal.ReleaseComObject(buffer); }
            return sample!;
        }
        catch { if (sample is not null) Marshal.ReleaseComObject(sample); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transform.ProcessMessage(unchecked((int)0x10000001), IntPtr.Zero);
        Marshal.ReleaseComObject(_transform);
        MFShutdown();
    }

    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApi
    {
        [PreserveSig] int IsSupported(ref Guid key);
        [PreserveSig] int IsModifiable(ref Guid key);
        [PreserveSig] int GetParameterRange(ref Guid key, IntPtr minimum, IntPtr maximum, IntPtr step);
        [PreserveSig] int GetParameterValues(ref Guid key, IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue(ref Guid key, IntPtr value);
        [PreserveSig] int GetValue(ref Guid key, IntPtr value);
        [PreserveSig] int SetValue(ref Guid key, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
