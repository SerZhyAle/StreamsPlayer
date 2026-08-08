using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0071: the player's side of the adaptive quality ceiling. The decision lives in
/// <see cref="AdaptiveQualityGovernor"/>; this partial forwards the two starvation signals the window
/// already raises, carries the ladder in from the network once, and applies what comes back.
///
/// <para>No timer is added: the probe clock is read on the existing two-second stats tick, and the
/// starvation signals are the stall the window already counts and the SP-0070 freeze it already catches.</para>
///
/// <para>SP-0076 adds the one thing the ladder cannot supply in time: what earlier sessions already knew.
/// The ladder is read from the network after the stream is live, so it can never reach the first open;
/// the record is a local file, so it can. That is the whole of the difference between paying a re-open to
/// arrive at the right rung and opening there.</para>
/// </summary>
public partial class PlayerWindow
{
    private readonly AdaptiveQualityGovernor _quality = new();
    private bool _ladderRequested;
    private bool _lowestRungReported;

    /// <summary>
    /// SP-0077: decides which of the two-second readings is worth a line. The engine chooses a rendition
    /// inside the ceiling itself and may change that choice mid-air, so the ceiling the log already
    /// carried was only ever half the story.
    /// </summary>
    private readonly PlayingRenditionTracker _rendition = new();
    private bool _renditionSkipReported;

    /// <summary>
    /// SP-0076: started in the constructor so the file read overlaps window layout, and awaited in
    /// <c>Loaded</c> before the first open - where an already-completed task resumes inline and costs
    /// nothing. Null for radio, which has no renditions to choose between and must not pay a read.
    /// </summary>
    private readonly Task<QualityRecollection>? _qualityRecall;

    /// <summary>
    /// What the record said, kept so the ladder that arrives later seeds its probe waits from the same
    /// snapshot the ceiling came from rather than from a second read of a file that may have moved.
    /// </summary>
    private QualityRecollection _recollection = QualityRecollection.Nothing;

    /// <summary>
    /// The ceiling this session opened with, from memory alone, until a real ladder replaces it. Cleared
    /// by <see cref="DropRememberedCeilingOnMiss"/> when the stream it was applied to never played.
    /// </summary>
    private StreamQualityRung? _rememberedCeiling;

    /// <summary>
    /// The last <see cref="AdaptiveQualityGovernor.MemoryRevision"/> written to disk. Comparing revisions
    /// on the existing tick is what catches the change no return value reports - a probe that survives its
    /// window clears that rung's record from inside <c>Observe</c>, which hands back nothing.
    /// </summary>
    private int _qualityMemorySaved;

    /// <summary>
    /// The ceiling the next media should be opened with. Read on the UI thread and carried into
    /// <c>StartMedia</c> as an argument rather than read from inside it: two of the three opens happen on
    /// a worker thread, and the governor is single-threaded by contract.
    /// <para>SP-0076: until a ladder has been read, the answer is what the record said. Falling back to
    /// null there would throw the ceiling away on the first reconnect - the stretch of a session most
    /// likely to need it, since it is the one before anything has been measured.</para>
    /// </summary>
    private StreamQualityRung? QualityCeiling => _quality.HasLadder ? _quality.Ceiling : _rememberedCeiling;

    /// <summary>
    /// SP-0076: reads what earlier sessions learned, before the first open rather than after it. Local
    /// disk only - nothing is fetched, so the ticket's "no new network request before air" holds by
    /// construction, and the ladder probe still runs no earlier than it did.
    /// <para>Logged on every path, including the ones that apply nothing: "no record for this source" and
    /// "the rule never ran" must not read the same in an archived log (criterion 2).</para>
    /// </summary>
    private async Task ApplyRememberedCeilingAsync()
    {
        if (_qualityRecall is null)
        {
            _log.Event("PLAYBACK QUALITY", "action=recall", "ceiling=none", "reason=not_video", $"url={_channel.Url}");
            return;
        }

        _recollection = await _qualityRecall;
        if (_closing)
        {
            return;
        }

        _rememberedCeiling = _recollection.Ceiling;
        _log.Event("PLAYBACK QUALITY",
            "action=recall",
            $"ceiling={Describe(_rememberedCeiling)}",
            $"reason={DescribeRecall(_recollection.CeilingRecall)}",
            $"url={_channel.Url}");
    }

    /// <summary>
    /// SP-0076: the remembered ceiling is a guess about a ladder nobody has read yet, so it is the prime
    /// suspect when a session never gets a picture at all. A source that re-encoded upward leaves every
    /// rendition above the remembered one, and libvlc's representation selector has nothing to fall back
    /// to when that happens - which would turn a cache into a channel that stopped working.
    /// <para>So the first re-open of a session that has never reached live drops it. Gated on
    /// <c>_firstLiveMs</c> rather than on <c>_reachedLive</c>, which every leg resets: a reconnect four
    /// minutes into a session that played fine says nothing about the ceiling.</para>
    /// </summary>
    private void DropRememberedCeilingOnMiss()
    {
        if (_rememberedCeiling is not { } missed || _firstLiveMs >= 0)
        {
            return;
        }

        _rememberedCeiling = null;
        // Same keys as the line that applied it, so one grep over action=recall reads the whole story of
        // what memory did to this session.
        _log.Event("PLAYBACK QUALITY",
            "action=recall",
            $"ceiling={missed.Describe()}",
            "reason=miss",
            $"url={_channel.Url}");
    }

    /// <summary>How a recall outcome is spelled in the log.</summary>
    private static string DescribeRecall(QualityCeilingRecall recall) => recall switch
    {
        QualityCeilingRecall.Applied => "ok",
        QualityCeilingRecall.Stale => "stale",
        QualityCeilingRecall.NoCeiling => "no_ceiling",
        _ => "no_record"
    };

    /// <summary>
    /// Asks the source what it offers, once per window and only after the stream is live. After live
    /// because the ticket forbids making the first open slower for a measurement; once because a
    /// flapping stream must not turn a fetch into a poll.
    /// </summary>
    private void RequestQualityLadder()
    {
        if (_ladderRequested)
        {
            return;
        }

        _ladderRequested = true;
        if (_channel.MediaKind != MediaKind.Video)
        {
            // Radio has no renditions to choose between (acceptance 5). Said out loud rather than
            // skipped in silence: in an archived log, "the rule did not apply" and "the rule never ran"
            // must not look the same - the same reason FlyleafVideoBackend reports its statistics gap.
            _log.Event("PLAYBACK QUALITY", "action=ladder", "rungs=0", "reason=not_video", $"url={_channel.Url}");
            return;
        }

        _ = LoadQualityLadderAsync();
    }

    /// <summary>
    /// The await resumes on the UI thread, which is what keeps the governor single-threaded. The probe
    /// reports every failure as a value, so there is nothing here to catch.
    /// </summary>
    private async Task LoadQualityLadderAsync()
    {
        var reading = await StreamQualityLadderProbe.ReadAsync(_channel.Url, _sessionCts.Token);
        if (_closing)
        {
            return;
        }

        if (reading.Rungs.Count < 2)
        {
            _log.Event("PLAYBACK QUALITY", "action=ladder", "rungs=0", $"reason={reading.Reason}", $"url={_channel.Url}");
            return;
        }

        // SP-0076: the same snapshot the ceiling came from, not a second read. The file may have been
        // rewritten by another window meanwhile, and seeding the waits from one record while the media
        // plays under another record's ceiling would make the session's own log unreadable.
        _quality.UseLadder(reading.Rungs, HealthNow, _recollection.Failures, _rememberedCeiling);
        _qualityMemorySaved = _quality.MemoryRevision;
        _log.Event("PLAYBACK QUALITY",
            "action=ladder",
            $"rungs={reading.Rungs.Count}",
            $"list={string.Join(",", reading.Rungs.Select(rung => rung.Describe()))}",
            // Without this a session that never probes is unreadable: "the wait had not elapsed" and
            // "this rung was already known bad" produce the same silence.
            $"memory={DescribeMemory(_recollection.Failures, reading.Rungs)}",
            // SP-0076: where the session entered the ladder, which is the only place "the remembered rung
            // is gone" becomes visible - at open time there was no ladder to compare it against.
            $"entry={_quality.LadderEntry}",
            $"rung={Describe(_quality.CurrentRung)}",
            $"url={_channel.Url}");
    }

    /// <summary>How a recalled record is spelled in the log, restricted to rungs this ladder still has.</summary>
    private static string DescribeMemory(
        IReadOnlyDictionary<int, int> recalled,
        IReadOnlyList<StreamQualityRung> ladder)
    {
        var parts = ladder
            .Where(rung => recalled.ContainsKey(rung.BandwidthBps))
            .Select(rung => $"{rung.Describe()}:{recalled[rung.BandwidthBps]}")
            .ToArray();
        return parts.Length == 0 ? "none" : string.Join(",", parts);
    }

    /// <summary>
    /// Persists this source's record when it has changed, and only then. Off the UI thread because it is
    /// a file round-trip; fire-and-forget because nothing downstream waits on it and a stream must never
    /// stall on a cache. Written the moment it changes rather than at close, so a crash, a power cut or
    /// an app shutdown that kills the window still leaves what this session learned.
    ///
    /// <para>The revision is marked saved before the write is attempted, so a failed write is not retried
    /// on the next tick. It does not need to be: every write carries this ladder's <em>complete</em>
    /// record, so the next change writes what the failed one would have.</para>
    /// </summary>
    private void SyncQualityMemory()
    {
        if (_quality.MemoryRevision == _qualityMemorySaved)
        {
            return;
        }

        _qualityMemorySaved = _quality.MemoryRevision;
        var url = _channel.Url;
        var rungs = _quality.Failures; // snapshot on the UI thread: the governor is single-threaded
        // SP-0076: the restriction in effect, snapshotted with the failures for the same reason - and it
        // is the ceiling rather than the current rung, so a session sitting on the top writes no cap and
        // the next one opens unrestricted.
        var ceiling = _quality.Ceiling;
        var recorded = rungs.Count == 0
            ? "none"
            : string.Join(",", rungs.Select(rung => $"{rung.BandwidthBps / 1000}k:{rung.Failures}"));
        _ = Task.Run(async () =>
        {
            var written = await QualityMemoryFile.RecordAsync(url, rungs, ceiling, DateTimeOffset.UtcNow);
            _log.Event("PLAYBACK QUALITY",
                "action=memory",
                $"rungs={recorded}",
                $"ceiling={Describe(ceiling)}",
                $"ok={written}",
                $"url={url}");
        });
    }

    /// <summary>
    /// The stream ran out of buffer while live - a stall, or an SP-0070 caught freeze.
    /// </summary>
    /// <param name="reopenNow">
    /// False when a recovery re-open is already about to happen for this same event: it will read the new
    /// ceiling on its way through <c>StartMedia</c>, so opening a second media here would be a double
    /// re-open of one stream.
    /// </param>
    private void NotifyQualityStarvation(string reason, bool reopenNow)
    {
        var before = Describe(_quality.CurrentRung);
        if (_quality.NotifyStarvation(HealthNow) is { } decision)
        {
            // Before the re-open, not after: a failed probe is exactly the fact worth surviving this
            // session, and the re-open is the moment most likely to be interrupted by a closing window.
            SyncQualityMemory();
            ApplyQualityDecision(decision, reason, before, reopenNow);
            return;
        }

        if (_quality.AtLowestRung && !_lowestRungReported)
        {
            // Said once, and worth saying: from here the starvation is the source's, not a rung the
            // player could still trade away, and a log that goes quiet would look like a rule that stopped.
            _lowestRungReported = true;
            _log.Event("PLAYBACK QUALITY", "action=hold", "reason=at_lowest", $"rung={before}", $"url={_channel.Url}");
        }
    }

    /// <summary>One observation on the existing stats tick: the probe clock, and nothing else.</summary>
    private void ObserveQuality()
    {
        if (_closing || !_reachedLive || _recoveryInFlight)
        {
            return; // a probe is a judgement about a stream that is playing; this one is not
        }

        var before = Describe(_quality.CurrentRung);
        var decision = _quality.Observe(HealthNow);
        // Unconditional: Observe also forgives a rung that survived its trial, and that change is
        // reported by no return value at all.
        SyncQualityMemory();
        if (decision is { } raised)
        {
            ApplyQualityDecision(raised, "probe", before, reopenNow: true);
        }
    }

    /// <summary>
    /// Logs the decision first and acts second, so the record survives even where the re-open is
    /// suppressed - the log is the only way a complaint about picture quality can be read back.
    /// </summary>
    private void ApplyQualityDecision(QualityDecision decision, string reason, string before, bool reopenNow)
    {
        _log.Event("PLAYBACK QUALITY",
            $"action={(decision.Kind == QualityChangeKind.StepDown ? "down" : "up")}",
            $"reason={reason}",
            $"from={before}",
            $"to={decision.Rung.Describe()}",
            // The rung and the ceiling are different answers: on the top rung there is no restriction to
            // apply, so this reads "none" while `to` still names the rendition the player moved to.
            $"ceiling={Describe(_quality.Ceiling)}",
            $"starvations={decision.Starvations}",
            $"url={_channel.Url}");

        if (!reopenNow || _closing || _recoveryInFlight)
        {
            return;
        }

        // Closes the watchdog's and the stall path's window here, on the UI thread, rather than a few
        // milliseconds later inside StartMedia on a worker thread. Both of those gate on _reachedLive,
        // and a tick landing in that gap would start a recovery for a media that is already being
        // replaced - the recovery leg is guarded by _recoveryInFlight, this one had nothing.
        _reachedLive = false;
        // SP-0072: the one cause the status line never distinguished. A quality change re-opens the
        // media exactly as a reconnect does, so the screen goes black for the same seconds - but calling
        // it a reconnect would blame the source for a step the player chose to take.
        NotifyInterrupted(PlaybackInterruptionKind.SwitchingQuality);

        // Deliberately not through RecoverAsync: a quality change must not spend the bounded recovery
        // budget, must not count as a reconnect, and must not put a "Reconnecting" label on a stream that
        // is being improved. Off the UI thread for the same reason the recovery leg is - the backend
        // serializes play against teardown, and a flapping stream must never freeze WPF.
        var ceiling = QualityCeiling;
        _ = Task.Run(() =>
        {
            if (!_closing)
            {
                StartMedia("quality", ceiling);
            }
        });
    }

    /// <summary>
    /// SP-0077: one reading of what the engine actually put on screen, on the same existing tick. The
    /// ceiling is a request; this is the answer, and until now the log held only the request.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately not gated on <c>_reachedLive</c>, unlike <see cref="ObserveQuality"/>. That gate
    /// exists because a probe is a judgement about a stream that is playing - but a leg that never gets a
    /// picture is exactly the leg whose "the engine reported nothing" line is worth having, so gating
    /// this would delete the evidence in the one case that needs it.</para>
    /// <para>Ends at <c>_log.Event</c> on purpose. The ticket's non-goals are all "do not act on this
    /// yet": the governor is not told, the memory record is not touched, and a rendition above the
    /// ceiling is written down rather than corrected - acting on an observation nothing has yet
    /// calibrated would risk both the observation and the rule that already works.</para>
    /// </remarks>
    private void ObserveRendition()
    {
        if (_closing)
        {
            return;
        }

        if (_channel.MediaKind != MediaKind.Video)
        {
            if (_renditionSkipReported)
            {
                return;
            }

            // Once, and said out loud for the same reason the ladder and the recall say it: in an
            // archived log, "this stream has no renditions" and "this rule never ran" must not look the
            // same. Radio reaches this window through resume and through RTSP-shaped entries.
            _renditionSkipReported = true;
            _log.Event("PLAYBACK QUALITY", "action=rendition", "to=none", "reason=not_video", $"url={_channel.Url}");
            return;
        }

        if (_rendition.Observe(_legCount, _backend.ReadRendition()) is not { } observation)
        {
            return; // the steady state, which is most of a session
        }

        var ceiling = QualityCeiling;
        _log.Event("PLAYBACK QUALITY",
            "action=rendition",
            // Criterion 2: the leg number is what tells a re-open from a switch the engine made on its
            // own, and it is printed beside the verdict so the log can be checked rather than trusted.
            $"cause={(observation.Cause == RenditionCause.Opened ? "open" : "switch")}",
            $"from={DescribePrevious(observation.From)}",
            $"to={DescribeShown(observation.To)}",
            // The request beside the result, so the two never have to be matched up by hand across lines.
            $"ceiling={Describe(ceiling)}",
            $"within={DescribeCompliance(PlayingRenditionTracker.Compare(observation.To, ceiling))}",
            $"engine={_backend.EngineName}",
            $"leg={_legCount}",
            $"url={_channel.Url}");
    }

    /// <summary>How a ceiling is spelled in the log; "none" is a real answer, not a missing one.</summary>
    private static string Describe(StreamQualityRung? rung) => rung?.Describe() ?? "none";

    /// <summary>
    /// The rendition a line moved away from. Absent means there was none - this leg's first answer.
    /// </summary>
    private static string DescribePrevious(VideoRendition? rendition) => rendition?.Describe() ?? "none";

    /// <summary>
    /// The rendition on screen. Absent means the engine did not say, which is a different fact from
    /// "there was none" - hence a different word from <see cref="DescribePrevious"/>.
    /// </summary>
    private static string DescribeShown(VideoRendition? rendition) => rendition?.Describe() ?? "unknown";

    /// <summary>
    /// How the ceiling fared, in four values rather than a flag (criterion 3). "The engine did not say"
    /// and "there was no ceiling to break" are both real answers and neither of them is "yes".
    /// </summary>
    private static string DescribeCompliance(CeilingCompliance compliance) => compliance switch
    {
        CeilingCompliance.Within => "yes",
        CeilingCompliance.Above => "no",
        CeilingCompliance.NoCeiling => "no_ceiling",
        _ => "unknown"
    };
}
