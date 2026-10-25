using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static StreamsPlayer.App.MediaFoundationNative;

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

        // S8-1: the output sample is created for every call, so every return after this point releases it - the
        // stream-change and need-more-input returns leaked a 16 KiB sample each (about 100 a second while broadcasting).
        try
        {
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
            if (output.Sample is not null)
            {
                Marshal.ReleaseComObject(output.Sample);
            }
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
                                ref transformInterfaceId, out object? instance) == 0)
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

}
