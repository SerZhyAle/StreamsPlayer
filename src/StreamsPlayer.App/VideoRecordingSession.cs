using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0121: one recording as the user asked for it - of the channel, not of one connection to it. A playback
/// re-open ends the engine's segment; the session keeps it, finishes it in the background, and the player starts
/// the next segment once the picture is back (decision 1a). When the recording ends every segment is reported
/// together.
/// <para>Thread-safe: an engine hands a segment over from whichever thread ended it - a reconnect's worker, a
/// teardown task - and inside its own gate, so a segment is always in the session before the call that could
/// have ended it returns. Finishing starts at once, on a worker, so it never waits for the recording to end.</para>
/// </summary>
internal sealed class VideoRecordingSession
{
    private readonly object _gate = new();
    private readonly List<Task<IReadOnlyList<SegmentResult>>> _finishing = [];
    private readonly CurrentLog _log;

    internal VideoRecordingSession(RecordingTarget target, CurrentLog log)
    {
        Target = target;
        _log = log;
        StartedAt = DateTimeOffset.Now;
    }

    internal RecordingTarget Target { get; }

    internal DateTimeOffset StartedAt { get; }

    internal int SegmentCount
    {
        get
        {
            lock (_gate)
            {
                return _finishing.Count;
            }
        }
    }

    /// <summary>Takes a segment the engine has stopped writing and starts finishing it.</summary>
    internal void Add(RecordingSegment segment)
    {
        lock (_gate)
        {
            _finishing.Add(FinishContainedAsync(segment));
        }
    }

    // S3-3: one segment whose finish throws must not take the other segments' outcomes down with it - the
    // WhenAll in CompleteAsync would rethrow and the whole recording would report nothing. The fault is logged
    // and the segment is reported as stranded where its file may still be.
    private async Task<IReadOnlyList<SegmentResult>> FinishContainedAsync(RecordingSegment segment)
    {
        try
        {
            return await RecordingFinisher.FinishAsync(segment, _log).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Event("RECORD FINISH", [$"engine={segment.Engine}", "fate=stranded", "reason=finish_fault", .. FaultLogFields.Of(exception)]);
            return [new SegmentResult(SegmentFate.Stranded, segment.KnownFile ?? segment.StagingDirectories.FirstOrDefault(), segment.Length, exception.GetType().Name)];
        }
    }

    /// <summary>Every segment's outcome, once all of them are finished. Call after the last segment was added.</summary>
    internal async Task<IReadOnlyList<SegmentResult>> CompleteAsync()
    {
        Task<IReadOnlyList<SegmentResult>>[] pending;
        lock (_gate)
        {
            pending = [.. _finishing];
        }

        var outcomes = await Task.WhenAll(pending).ConfigureAwait(false);
        return [.. outcomes.SelectMany(outcome => outcome)];
    }
}
