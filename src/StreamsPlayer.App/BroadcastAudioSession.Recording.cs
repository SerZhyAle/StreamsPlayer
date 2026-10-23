using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal sealed partial class BroadcastAudioSession
{
    private readonly List<Task<IReadOnlyList<SegmentResult>>> _recordingFinishes = [];
    private CurrentLog? _recordingLog;
    private DateTimeOffset? _recordingStartedAt;

    internal DateTimeOffset? RecordingStartedAt
    {
        get { lock (_gate) return _recordingStartedAt; }
        private set { _recordingStartedAt = value; }
    }

    internal event Action<IReadOnlyList<SegmentResult>, bool>? RecordingCompleted;

    internal bool IsRecording
    {
        get { lock (_gate) return RecordingStartedAt is not null; }
    }

    internal bool IsRecordingWriting
    {
        get { lock (_gate) return IsRecording && _playback?.IsRecordingWriting == true; }
    }

    internal bool IsRecordingFinishing
    {
        get { lock (_gate) return _recordingFinishes.Any(finish => !finish.IsCompleted); }
    }

    internal bool CanRecord
    {
        get { lock (_gate) return !_disposed && State == BroadcastLegState.Playing && _playback?.CanRecord == true; }
    }

    internal async Task<CaptureDestination?> StartRecordingAsync(IReadOnlyList<string> chain, CurrentLog log)
    {
        var destination = await Task.Run(() => CaptureFolders.FirstWritable(chain)).ConfigureAwait(false);
        if (destination is null) return null;

        return await Task.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed || State != BroadcastLegState.Playing || IsRecording || IsRecordingFinishing || _playback is null)
                {
                    return null;
                }

                var target = new RecordingTarget(destination.Folder, StreamTitleFormatter.Display(_channel.Title),
                    destination.Rest, CaptureKind.StreamAudio);
                if (!_playback.StartRecording(target, log)) return null;
                _recordingLog = log;
                RecordingStartedAt = DateTimeOffset.Now;
                return destination;
            }
        }).ConfigureAwait(false);
    }

    internal Task StopRecordingAsync() => Task.Run(() =>
    {
        lock (_gate) EndRecordingLocked();
    });

    /// <summary>Snapshot after Dispose: every segment has been handed over before the window's Closed event.</summary>
    internal Task RecordingFinish
    {
        get { lock (_gate) return Task.WhenAll(_recordingFinishes); }
    }

    private void EndRecordingLocked()
    {
        if (RecordingStartedAt is null) return;
        RecordingStartedAt = null;
        if (_playback?.EndRecording() is { } recorder && _recordingLog is { } log)
        {
            _recordingFinishes.Add(FinishRecordingAsync(recorder, log));
        }
    }

    private async Task<IReadOnlyList<SegmentResult>> FinishRecordingAsync(BroadcastAudioRecorder recorder, CurrentLog log)
    {
        var segment = await recorder.Completion.ConfigureAwait(false);
        IReadOnlyList<SegmentResult> results;
        try
        {
            results = await RecordingFinisher.FinishAsync(segment, log).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Event("RECORD FINISH", ["engine=broadcast_tap", "fate=stranded", .. FaultLogFields.Of(exception)]);
            results = [new SegmentResult(SegmentFate.Stranded, segment.StagingDirectories.FirstOrDefault(),
                segment.Length, exception.GetType().Name)];
        }

        RecordingCompleted?.Invoke(results, recorder.Failed);
        return results;
    }
}
