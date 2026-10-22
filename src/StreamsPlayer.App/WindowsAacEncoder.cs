using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0217 constraint 10 / the owner's video-encoder decision, on the audio side: the AAC-LC encoder
/// Windows ships (Media Foundation), with the ADTS payload type, so the encoder's own output is the
/// section 2.1 payload - every frame carries its ADTS header and a late joiner decodes from the next
/// frame. No new third-party binary; where the OS encoder is absent the constructor fails and the
/// sources are not offered.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsAacEncoder : IDisposable
{
    private const int MfVersion = 0x20070;
    private const int MfStartupFull = 0;
    private const int MftEnumSyncMft = 0x00000001;
    private const int MftEnumLocalMft = 0x00000010;
    private const int MftMessageNotifyBeginStreaming = unchecked((int)0x10000000);
    private const int MftMessageNotifyEndStreaming = unchecked((int)0x10000001);
    private const int MftMessageCommandDrain = 0x1;
    private const int MfTransformStreamChange = unchecked((int)0xC00D6D61);
    private const int OutputSampleCapacityBytes = 16384;

    // The vtable keys and shapes of mfapi.h, copied to locals wherever a REFGUID parameter needs one.
    private static readonly Guid MajorTypeAudio = new("73647561-0000-0010-8000-00AA00389B71"); // MFMediaType_Audio
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71"); // MFAudioFormat_PCM (WAVE_FORMAT_PCM)
    private static readonly Guid SubtypeAac = new("00001610-0000-0010-8000-00AA00389B71"); // MFAudioFormat_AAC (WAVE_FORMAT_MPEG_HEAAC)
    private static readonly Guid MftCategoryAudioEncoder = new("91C64BD0-F91E-4D8C-9276-DB248279D975");
    private static readonly Guid MtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MtAudioChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    private static readonly Guid MtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    private static readonly Guid MtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    private static readonly Guid MtAudioAverageBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    private static readonly Guid MtTransformClsid = new("6821C42B-65A4-4E82-99BC-9A88205ECD0C"); // MFT_TRANSFORM_CLSID_Attribute
    private static readonly Guid MtAacPayloadType = new("BFBABE79-7434-4D1C-94F0-72A3B9E17188");
    private static readonly Guid IidImfTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    private bool _startupHeld;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _bitrateKbps;
    private long _position100ns;
    private IMFTransform? _session;
    private int _sessionThreadId;
    private bool _faulted;
    private bool _disposed;

    /// <exception cref="InvalidOperationException">The OS encoder is absent or refuses the requested
    /// shape; the caller does not offer the audio sources and says so in Settings (constraint 10).</exception>
    internal WindowsAacEncoder(int sampleRate, int channels, int bitrateKbps)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _bitrateKbps = bitrateKbps;
        try
        {
            _startupHeld = MFStartup(MfVersion, MfStartupFull) == 0;
            // Validation on this thread only: the session Encode later drives is created on
            // the capture thread (see Encode). An RCW created here and called from the capture
            // thread is the recorded 0xC0000005 (temp/SP-0214/encoder-diag, phase F: raw
            // cross-thread calls on the free-threaded MFT are safe, a foreign-apartment RCW is not).
            Marshal.ReleaseComObject(CreateSession());
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            Dispose();
            throw;
        }
        catch
        {
            Dispose();
            throw new InvalidOperationException("The AAC encoder could not be configured.");
        }
    }

    /// <summary>
    /// Feeds one buffer of 16-bit PCM and returns the ADTS frames it produced - zero, one or several
    /// complete frames, never a partial one, because the encoder emits whole access units.
    /// </summary>
    internal byte[] Encode(ReadOnlySpan<byte> pcm)
    {
        if (_faulted || _disposed || pcm.Length == 0)
        {
            return [];
        }

        var transform = _session;
        if (transform is null)
        {
            // The session is created on the thread that feeds it - the capture loop's. One encoder
            // instance is fed by one capture loop; a second feeding thread would interleave
            // timestamps, which the broadcast never does. A failure here (the machine changed
            // under a validated encoder) degrades to silence instead of killing the capture thread.
            try
            {
                transform = CreateSession();
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                _faulted = true;
                return [];
            }

            _session = transform;
            _sessionThreadId = Environment.CurrentManagedThreadId;
        }

        WriteInput(transform, pcm);
        var output = new List<byte>(2048);
        while (ReadOutput(transform, out var frame))
        {
            output.AddRange(frame);
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        _disposed = true;
        var session = _session;
        _session = null;
        if (session is not null)
        {
            if (_sessionThreadId == Environment.CurrentManagedThreadId)
            {
                _ = session.ProcessMessage(MftMessageNotifyEndStreaming, IntPtr.Zero);
                _ = session.ProcessMessage(MftMessageCommandDrain, IntPtr.Zero);
                while (ReadOutput(session, out _))
                {
                }

                Marshal.ReleaseComObject(session);
            }
            else
            {
                // The session's RCW belongs to its capture thread, which has already ended; its
                // finalizer releases the MFT, and the platform hold stays for the process (an
                // MFShutdown under a still-referenced MFT is not a documented shape). The tail
                // frames are dropped, which is what a stream ending mid-frame always did.
                _startupHeld = false;
            }
        }

        if (_startupHeld)
        {
            _ = MFShutdown();
            _startupHeld = false;
        }
    }

    /// <summary>Creates and configures the MFT on the calling thread; the returned RCW is safe to
    /// call only on that thread (see the constructor and Encode).</summary>
    private IMFTransform CreateSession()
    {
        var transform = FindAacEncoder();
        if (transform is null)
        {
            throw new InvalidOperationException("No AAC encoder is registered on this machine.");
        }

        SetInputType(transform);
        SetOutputType(transform);
        _ = transform.ProcessMessage(MftMessageNotifyBeginStreaming, IntPtr.Zero);
        return transform;
    }

    private void WriteInput(IMFTransform transform, ReadOnlySpan<byte> pcm)
    {
        _ = MFCreateSample(out IMFSample? sample);
        _ = MFCreateMemoryBuffer(pcm.Length, out IMFMediaBuffer? buffer);
        try
        {
            if (buffer is null || sample is null)
            {
                return;
            }

            _ = buffer.Lock(out IntPtr data, out _, out _);
            try
            {
                Marshal.Copy(pcm.ToArray(), 0, data, pcm.Length);
            }
            finally
            {
                _ = buffer.Unlock();
            }

            buffer.SetCurrentLength(pcm.Length);
            sample.AddBuffer(buffer);
            // The Windows AAC MFT refuses an untimestamped sample with MF_E_NO_SAMPLE_TIMESTAMP,
            // so each block carries its running position and length (temp/SP-0214/encoder-diag,
            // phase C).
            var samples = pcm.Length / (2 * _channels);
            var duration100ns = samples * 10_000_000L / _sampleRate;
            _ = sample.SetSampleTime(_position100ns);
            _ = sample.SetSampleDuration(duration100ns);
            _position100ns += duration100ns;
            _ = transform.ProcessInput(0, sample, 0);
        }
        finally
        {
            if (sample is not null)
            {
                Marshal.ReleaseComObject(sample);
            }

            if (buffer is not null)
            {
                Marshal.ReleaseComObject(buffer);
            }
        }
    }

    /// <summary>Reads one encoder output; false on need-more-input, and a stream change is accepted by adopting the encoder's own current type.</summary>
    private static bool ReadOutput(IMFTransform transform, out byte[] frame)
    {
        frame = [];
        var output = new MftOutputDataBuffer
        {
            // The Windows AAC MFT does not provide its own output sample: the caller hands it an
            // empty one. A NULL pSample is refused - on Windows 26200 with a 0xC0000005 instead of
            // the documented stream error (temp/SP-0214/encoder-diag, phase C).
            Sample = CreateEmptySample(OutputSampleCapacityBytes)
        };

        if (output.Sample is null)
        {
            return false;
        }

        var result = transform.ProcessOutput(0, 1, ref output, out _);
        if (output.Events != IntPtr.Zero)
        {
            Marshal.Release(output.Events);
        }

        if (result == MfTransformStreamChange)
        {
            // The encoder chose its final type after the first input; adopt it and ask again.
            return AdoptCurrentOutputType(transform);
        }

        if (result != 0 || output.Sample is null)
        {
            return false;
        }

        try
        {
            if (output.Sample.ConvertToContiguousBuffer(out IMFMediaBuffer? buffer) != 0 || buffer is null)
            {
                return false;
            }

            try
            {
                _ = buffer.Lock(out IntPtr data, out _, out int length);
                if (length == 0)
                {
                    return false;
                }

                frame = new byte[length];
                Marshal.Copy(data, frame, 0, (int)length);
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(output.Sample);
        }
    }

    /// <summary>An empty sample with one zero-length buffer, for the MFT to fill.</summary>
    private static IMFSample? CreateEmptySample(int capacityBytes)
    {
        if (MFCreateSample(out IMFSample? sample) != 0 || sample is null
            || MFCreateMemoryBuffer(capacityBytes, out IMFMediaBuffer? buffer) != 0 || buffer is null)
        {
            if (sample is not null)
            {
                Marshal.ReleaseComObject(sample);
            }

            return null;
        }

        try
        {
            _ = buffer.SetCurrentLength(0);
            _ = sample.AddBuffer(buffer);
            return sample;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    private static bool AdoptCurrentOutputType(IMFTransform transform)
    {
        if (transform.GetOutputCurrentType(0, out IMFMediaType? type) != 0 || type is null)
        {
            return false;
        }

        try
        {
            return transform.SetOutputType(0, type, 0) == 0;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static IMFTransform? FindAacEncoder()
    {
        var input = new MftRegisterTypeInformation { MajorType = MajorTypeAudio, Subtype = SubtypePcm };
        var output = new MftRegisterTypeInformation { MajorType = MajorTypeAudio, Subtype = SubtypeAac };
        var result = MFTEnumEx(MftCategoryAudioEncoder, MftEnumSyncMft | MftEnumLocalMft, ref input, ref output,
            out IntPtr activations, out uint count);
        if (result != 0 || count == 0 || activations == IntPtr.Zero)
        {
            if (activations != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(activations);
            }

            return null;
        }

        IMFTransform? transform = null;
        try
        {
            for (uint index = 0; index < count && transform is null; index++)
            {
                var pointer = Marshal.ReadIntPtr(activations, (int)(index * IntPtr.Size));
                if (Marshal.GetObjectForIUnknown(pointer) is not IMFActivate activate)
                {
                    continue;
                }

                try
                {
                    var transformInterfaceId = IidImfTransform;
                    if (activate.ActivateObject(ref transformInterfaceId, out object activated) == 0)
                    {
                        transform = activated as IMFTransform;
                    }

                    if (transform is null)
                    {
                        // SP-0214, 2026-10-15: the recorded MF_E_ATTRIBUTENOTFOUND "refusal" of
                        // ActivateObject was the derived-interface slot bug documented at the flat
                        // vtable declarations below, not an OS behavior - with the flat vtables
                        // activation succeeds. The CLSID fallback stays for an activation object that
                        // genuinely refuses: the same object, reached the way its registration says.
                        var clsidKey = MtTransformClsid;
                        if (activate.GetGUID(ref clsidKey, out var clsid) == 0
                            && CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer,
                                ref transformInterfaceId, out object instance) == 0)
                        {
                            transform = instance as IMFTransform;
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }
        }
        finally
        {
            for (uint index = 0; index < count; index++)
            {
                Marshal.Release(Marshal.ReadIntPtr(activations, (int)(index * IntPtr.Size)));
            }

            Marshal.FreeCoTaskMem(activations);
        }

        return transform;
    }

    private void SetInputType(IMFTransform transform)
    {
        if (MFCreateMediaType(out IMFMediaType? type) != 0 || type is null)
        {
            throw new InvalidOperationException("The media type could not be created.");
        }

        try
        {
            SetAudioType(type, SubtypePcm, _sampleRate, _channels);
            var bitsKey = MtAudioBitsPerSample;
            type.SetUINT32(ref bitsKey, 16);
            if (transform.SetInputType(0, type, 0) != 0)
            {
                throw new InvalidOperationException("The AAC encoder refused the PCM input type.");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private void SetOutputType(IMFTransform transform)
    {
        if (MFCreateMediaType(out IMFMediaType? type) != 0 || type is null)
        {
            throw new InvalidOperationException("The media type could not be created.");
        }

        try
        {
            SetAudioType(type, SubtypeAac, _sampleRate, _channels);
            // Payload type 1: the frames leave the encoder ADTS-framed, the section 2.1 payload itself.
            var payloadKey = MtAacPayloadType;
            type.SetUINT32(ref payloadKey, 1);
            var bytesPerSecondKey = MtAudioAverageBytesPerSecond;
            type.SetUINT32(ref bytesPerSecondKey, (uint)(_bitrateKbps * 1000 / 8));
            if (transform.SetOutputType(0, type, 0) == 0)
            {
                return;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }

        // SP-0214, found on Windows 11 26200: the encoder refuses the hand-built type with
        // MF_E_ATTRIBUTENOTFOUND but accepts its own available type of the same shape, so the
        // menu GetOutputAvailableType offers is the fallback - with the ADTS payload kept.
        var chosen = ChooseAvailableOutputType(transform);
        if (chosen is null)
        {
            throw new InvalidOperationException("The AAC encoder refused the AAC output type.");
        }

        try
        {
            var payloadKey = MtAacPayloadType;
            chosen.SetUINT32(ref payloadKey, 1);
            if (transform.SetOutputType(0, chosen, 0) != 0)
            {
                throw new InvalidOperationException("The AAC encoder refused the AAC output type.");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(chosen);
        }
    }

    private IMFMediaType? ChooseAvailableOutputType(IMFTransform transform)
    {
        var targetBytesPerSecond = (uint)(_bitrateKbps * 1000 / 8);
        for (uint index = 0; ; index++)
        {
            if (transform.GetOutputAvailableType(0, index, out var candidate) != 0 || candidate is null)
            {
                return null;
            }

            var matched = false;
            try
            {
                var channelsKey = MtAudioChannels;
                var sampleRateKey = MtAudioSamplesPerSecond;
                var bytesPerSecondKey = MtAudioAverageBytesPerSecond;
                matched = candidate.GetUINT32(ref channelsKey, out var candidateChannels) == 0
                    && candidate.GetUINT32(ref sampleRateKey, out var candidateRate) == 0
                    && candidate.GetUINT32(ref bytesPerSecondKey, out var candidateBytes) == 0
                    && candidateChannels == (uint)_channels
                    && candidateRate == (uint)_sampleRate
                    && candidateBytes == targetBytesPerSecond;
            }
            finally
            {
                if (!matched)
                {
                    Marshal.ReleaseComObject(candidate);
                }
            }

            if (matched)
            {
                return candidate;
            }
        }
    }

    /// <summary>The members the input and the output type share: major type, subtype, channels, sample rate.</summary>
    private static void SetAudioType(IMFMediaType type, Guid subtype, int sampleRate, int channels)
    {
        var majorTypeKey = MtMajorType;
        var majorType = MajorTypeAudio;
        type.SetGUID(ref majorTypeKey, ref majorType);
        var subtypeKey = MtSubtype;
        var subtypeValue = subtype;
        type.SetGUID(ref subtypeKey, ref subtypeValue);
        var channelsKey = MtAudioChannels;
        type.SetUINT32(ref channelsKey, (uint)channels);
        var sampleRateKey = MtAudioSamplesPerSecond;
        type.SetUINT32(ref sampleRateKey, (uint)sampleRate);
    }

    private struct MftRegisterTypeInformation
    {
        public Guid MajorType;
        public Guid Subtype;
    }

    private struct MftOutputDataBuffer
    {
        public uint StreamId;
        public IMFSample? Sample;
        public uint Status;
        public IntPtr Events;
    }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType([MarshalAs(UnmanagedType.Interface)] out IMFMediaType? type);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample([MarshalAs(UnmanagedType.Interface)] out IMFSample? sample);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(int maximumLength, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer? buffer);

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(Guid category, int flags, ref MftRegisterTypeInformation inputType,
        ref MftRegisterTypeInformation outputType, out IntPtr activations, out uint count);

    private const uint ClsctxInprocServer = 1;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object? instance);

    // The vtables exactly as mfobjects.h declares them, FLAT: .NET's built-in COM interop computes
    // a derived ComImport interface's method slots without the base interface's methods, so
    // IMFSample, IMFMediaType and IMFActivate each spell out the full IMFAttributes vtable (its 30
    // real methods, in order - mfobjects.h declares no Clone) before their own (SP-0214, 2026-10-15;
    // the inheritance shape dispatched every own method 30 slots too early and was misread as an OS
    // AAC encoder crash, 0xC0000005 on Windows 26200 - the OS encoder is healthy, see
    // temp/SP-0214/encoder-diag). The setters used here are in the attributes' second half, so
    // every slot above them is declared too.
    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[]? value, int size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFMediaType theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[]? value, int size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFMediaType destination);
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType theirs, out int flags);
        [PreserveSig] int GetRepresentation(ref Guid representationId, out IntPtr representation);
        [PreserveSig] int FreeRepresentation(ref Guid representationId, IntPtr representation);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFSample theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[]? value, int size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFSample destination);
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer? buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer? buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("BF94C121-5B05-4E6F-8000-BA598961414D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint inputMinimum, out uint inputMaximum, out uint outputMinimum, out uint outputMaximum);
        [PreserveSig] int GetStreamCount(out uint inputStreams, out uint outputStreams);
        [PreserveSig] int GetStreamIDs(uint inputIdsSize, IntPtr inputIds, uint outputIdsSize, IntPtr outputIds);
        [PreserveSig] int GetInputStreamInfo(uint streamId, IntPtr info);
        [PreserveSig] int GetOutputStreamInfo(uint streamId, IntPtr info);
        [PreserveSig] int GetAttributes(out IMFAttributes? attributes);
        [PreserveSig] int GetInputStreamAttributes(uint streamId, out IMFAttributes? attributes);
        [PreserveSig] int GetOutputStreamAttributes(uint streamId, out IMFAttributes? attributes);
        [PreserveSig] int DeleteInputStream(uint streamId);
        [PreserveSig] int AddInputStreams(uint count, IntPtr streamIds);
        [PreserveSig] int GetInputAvailableType(uint streamId, uint typeIndex, out IMFMediaType? type);
        [PreserveSig] int GetOutputAvailableType(uint streamId, uint typeIndex, out IMFMediaType? type);
        [PreserveSig] int SetInputType(uint streamId, IMFMediaType? type, uint flags);
        [PreserveSig] int SetOutputType(uint streamId, IMFMediaType? type, uint flags);
        [PreserveSig] int GetInputCurrentType(uint streamId, out IMFMediaType? type);
        [PreserveSig] int GetOutputCurrentType(uint streamId, out IMFMediaType? type);
        [PreserveSig] int GetInputStatus(uint streamId, out uint flags);
        [PreserveSig] int GetOutputStatus(out uint flags);
        [PreserveSig] int SetOutputBounds(long minimum, long maximum);
        [PreserveSig] int ProcessEvents(uint streamId, IntPtr events);
        [PreserveSig] int ProcessMessage(int message, IntPtr parameter);
        [PreserveSig] int ProcessInput(uint streamId, IMFSample sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint count, ref MftOutputDataBuffer outputs, out uint status);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFActivate theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out] char[]? value, int size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFActivate destination);
        [PreserveSig] int ActivateObject(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }
}
