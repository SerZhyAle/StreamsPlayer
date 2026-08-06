using System.Diagnostics;
using LibVLCSharp.Shared;
using StreamsPlayer.Core;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0053: measures what a stream is actually sending, for the About window.
/// </summary>
/// <remarks>
/// A headless sibling of <see cref="VideoFrameCaptureService"/>: its own engine instance with dummy
/// outputs, so nothing is drawn and nothing is heard, a hard deadline, and the native stop pushed off
/// the calling thread because it blocks on a flapping stream. The window that owns the measurement can
/// be closed at any moment, so every exit path here is a value - never an exception at the caller.
/// </remarks>
internal static class StreamTransmissionProbe
{
    /// <summary>
    /// How long the stream is watched before the reading is reported. Measured, not guessed: an
    /// adaptive HLS source announces its lowest rendition first and only climbs to the real one later
    /// (320x184 at one second, 1920x1080 at ten on the reference stream), so reporting the first
    /// description that arrives reports the wrong stream.
    /// </summary>
    private static readonly TimeSpan ObservationTime = TimeSpan.FromSeconds(12);

    /// <summary>The point at which a stream that has said nothing at all is declared unreachable.</summary>
    private static readonly TimeSpan DescriptionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How many of the most recent rate samples the reported figure is the median of. The counter is
    /// noisy on a chunked source - the reference HLS stream swings between 133 and 13021 kbps within one
    /// window - so the last sample alone reports whichever burst it landed in.
    /// </summary>
    private const int RateSampleWindow = 8;

    /// <summary>
    /// libvlc reports its rate counters in bytes per millisecond; the product's unit is kilobits per
    /// second. Confirmed against a 128 kbps station, which reads back as exactly 128.
    /// </summary>
    private const double BytesPerMillisecondToKilobitsPerSecond = 8000d;

    /// <summary>
    /// Measures the stream at <paramref name="url"/>, or reports null when it cannot be described.
    /// </summary>
    /// <remarks>
    /// The whole measurement is pushed to the thread pool, not merely awaited from the caller. Its
    /// caller is a window's Loaded handler, so without this every continuation would resume on the
    /// interface thread: building the engine, reading the description twice a second, and releasing the
    /// native handles would all land there. Building the engine alone is the expensive part, and on the
    /// interface thread it runs before the window has painted - so the "measuring" line the user is
    /// supposed to see while waiting would appear only once the wait was nearly over.
    /// </remarks>
    public static Task<StreamTransmission?> MeasureAsync(string url, CancellationToken cancellationToken) =>
        // CancellationToken.None: cancellation is this method's own business and is reported as a null
        // reading. Handing the token to Task.Run would instead throw at a caller promised an answer.
        Task.Run(() => MeasureCoreAsync(url, cancellationToken), CancellationToken.None);

    private static async Task<StreamTransmission?> MeasureCoreAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            return null;
        }

        LibVLCSharp.Shared.Core.Initialize();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DescriptionTimeout);
        // Dummy outputs: this opens a stream to describe it, never to show or play it.
        using var libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--quiet", "--avcodec-hw=none", "--vout=dummy", "--aout=dummy");
        using var mediaPlayer = new VlcMediaPlayer(libVlc) { Mute = true, Volume = 0 };
        using var media = new Media(libVlc, address);
        media.AddOption(":network-caching=2000");
        media.AddOption(":live-caching=2000");

        try
        {
            if (!mediaPlayer.Play(media))
            {
                return null;
            }

            StreamTransmission? reading = null;
            var rates = new List<double>();
            var watched = Stopwatch.StartNew();
            while (reading is null || watched.Elapsed < ObservationTime)
            {
                await Task.Delay(PollInterval, deadline.Token);
                var sample = Read(media);
                if (!Describes(sample))
                {
                    continue;
                }

                reading = Merge(reading, sample);
                if (sample.ObservedKilobitsPerSecond is { } rate)
                {
                    rates.Add(rate);
                }
            }

            return reading with { ObservedKilobitsPerSecond = MedianRate(rates) };
        }
        catch (OperationCanceledException)
        {
            return null; // the deadline passed, or the window that asked for this closed
        }
        catch (VLCException)
        {
            return null;
        }
        finally
        {
            await StopAsync(mediaPlayer);
        }
    }

    /// <summary>
    /// One snapshot of an open media. Shared with <see cref="LibVlcVideoBackend"/> so a measured channel
    /// reads identically whether it was probed here or was already playing.
    /// </summary>
    internal static StreamTransmission Read(Media media)
    {
        var tracks = media.Tracks;
        // Not the first track of each kind: an adaptive source lists every rendition, and its earliest
        // entries are placeholders carrying a zeroed frame rate and no sample rate. The fullest
        // descriptor is the one that answers what the stream sends.
        var video = tracks.Where(track => track.TrackType == TrackType.Video)
            .OrderByDescending(track => track.Data.Video.Width)
            .ThenByDescending(track => track.Data.Video.FrameRateDen)
            .FirstOrDefault();
        var audio = tracks.Where(track => track.TrackType == TrackType.Audio)
            .OrderByDescending(track => track.Data.Audio.Rate)
            .ThenByDescending(track => track.Data.Audio.Channels)
            .FirstOrDefault();

        return new StreamTransmission(
            CodecName(media, TrackType.Video, video),
            (int)video.Data.Video.Width,
            (int)video.Data.Video.Height,
            FrameRate(video),
            CodecName(media, TrackType.Audio, audio),
            (int)audio.Data.Audio.Channels,
            (int)audio.Data.Audio.Rate,
            DataRate(media.Statistics));
    }

    // The demux counter is the media's own rate and is what the advertised bitrate claims to be; the
    // input counter is the network read rate and stays at zero for a playlist-driven source, where only
    // the child input does the reading.
    private static double? DataRate(MediaStats statistics)
    {
        if (statistics.DemuxBitrate > 0)
        {
            return statistics.DemuxBitrate * BytesPerMillisecondToKilobitsPerSecond;
        }

        return statistics.InputBitrate > 0 ? statistics.InputBitrate * BytesPerMillisecondToKilobitsPerSecond : null;
    }

    /// <summary>
    /// The representative data rate for the observation window. Median rather than mean: a live stream's
    /// instantaneous rate spikes while the buffer fills and drops to nothing during a stall, and one such
    /// sample would drag an average far from what the stream actually sends. Null when nothing was read.
    /// </summary>
    private static double? MedianRate(List<double> rates)
    {
        if (rates.Count == 0)
        {
            return null;
        }

        rates.Sort();
        var middle = rates.Count / 2;
        return rates.Count % 2 == 1 ? rates[middle] : (rates[middle - 1] + rates[middle]) / 2;
    }

    private static bool Describes(StreamTransmission sample) =>
        sample.VideoCodec is not null || sample.AudioCodec is not null;

    // Keeps the fullest answer seen during the observation window. Width and frame rate travel together,
    // as do channel count and sample rate: mixing two renditions would describe a stream that is not
    // being sent.
    private static StreamTransmission Merge(StreamTransmission? previous, StreamTransmission sample)
    {
        if (previous is null)
        {
            return sample;
        }

        var video = sample.Width >= previous.Width ? sample : previous;
        var audio = sample.SampleRate >= previous.SampleRate ? sample : previous;
        return new StreamTransmission(
            video.VideoCodec ?? previous.VideoCodec,
            video.Width,
            video.Height,
            video.FrameRate,
            audio.AudioCodec ?? previous.AudioCodec,
            audio.AudioChannels,
            audio.SampleRate,
            sample.ObservedKilobitsPerSecond ?? previous.ObservedKilobitsPerSecond);
    }

    private static async Task StopAsync(VlcMediaPlayer mediaPlayer)
    {
        try
        {
            await Task.Run(mediaPlayer.Stop);
        }
        catch (VLCException)
        {
            // Disposal by the using declarations remains mandatory even if the native stop reports failure.
        }
    }

    // libvlc's own description ("H264 - MPEG-4 AVC (part 10)") is what a user can recognise and quote;
    // the raw fourcc is the fallback for a codec this build of libvlc has no name for.
    private static string? CodecName(Media media, TrackType type, MediaTrack track)
    {
        if (track.Codec == 0)
        {
            return null;
        }

        var description = media.CodecDescription(type, track.Codec);
        return string.IsNullOrWhiteSpace(description) ? FourCharacterCode(track.Codec) : description;
    }

    private static string FourCharacterCode(uint codec) => new(
    [
        (char)(codec & 0xFF),
        (char)((codec >> 8) & 0xFF),
        (char)((codec >> 16) & 0xFF),
        (char)((codec >> 24) & 0xFF)
    ]);

    private static double? FrameRate(MediaTrack video) =>
        video.Data.Video.FrameRateDen > 0
            ? video.Data.Video.FrameRateNum / (double)video.Data.Video.FrameRateDen
            : null;
}
