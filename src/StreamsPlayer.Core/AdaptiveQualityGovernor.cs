namespace StreamsPlayer.Core;

/// <summary>Why the governor moved the ceiling.</summary>
public enum QualityChangeKind
{
    /// <summary>The source could not deliver the current rung in real time.</summary>
    StepDown,

    /// <summary>A trial of the next rung up, after an interval with no starvation.</summary>
    Probe
}

/// <summary>
/// A ceiling change the player should apply. <paramref name="Starvations"/> is the count that produced a
/// <see cref="QualityChangeKind.StepDown"/> (zero for a probe) - it travels with the decision so the log
/// can say why without the App keeping a second copy of the governor's state.
/// </summary>
public readonly record struct QualityDecision(StreamQualityRung Rung, QualityChangeKind Kind, int Starvations);

/// <summary>SP-0076: where a session entered the ladder, and why.</summary>
public enum QualityLadderEntry
{
    /// <summary>Nothing was remembered, so the session starts where it always did.</summary>
    Top,

    /// <summary>An earlier session's ceiling placed this one below the top without paying a re-open.</summary>
    Remembered,

    /// <summary>
    /// A ceiling was remembered and no rung of today's ladder fits inside it - the source re-encoded and
    /// the record describes renditions it no longer offers. The session falls back to today's behaviour.
    /// </summary>
    Missed
}

/// <summary>
/// SP-0071: picks the highest rung of an adaptive stream that this source is actually delivering.
/// Like <see cref="SignalHealthMonitor"/>, it decides from what the player already observes: time is a
/// parameter on every call and never read ambiently, and nothing here knows about HTTP, a media engine
/// or a window - so the whole rule is decidable in tests rather than only on a bad network.
/// </summary>
/// <remarks>
/// <para><b>A step down is decided by starvation, not by a speed reading.</b> The buffer running dry is
/// the one measurement that says delivery lost to real-time consumption over that interval, whatever any
/// number claims. Both cheaper readings were rejected against the 2026-08-08 evidence: comparing
/// delivered bytes with the rung's declared <c>BANDWIDTH</c> condemns healthy renditions, because
/// <c>BANDWIDTH</c> is the peak and the mean runs well under it; and an instantaneous rate sample is the
/// ticket's first risk made real - that source bursts, so its measured rate swings from zero to well
/// above the rung between two samples.</para>
/// <para><b>Going back up is a trial, not a deduction.</b> On a lower rung the player is not saturating
/// the link, so nothing it can measure proves the higher rung has become deliverable. The probe is
/// therefore a real switch, and the doubling wait is what keeps a permanently slow source from paying
/// for one re-open a minute forever.</para>
/// <para>The type is not thread-safe: the player feeds it from the UI thread only.</para>
/// </remarks>
public sealed class AdaptiveQualityGovernor
{
    /// <summary>
    /// How far back starvations are counted. The reported source starved every 44-121 s, so two inside
    /// this window is a pattern rather than a coincidence.
    /// </summary>
    public static readonly TimeSpan StarvationWindow = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How many starvations inside <see cref="StarvationWindow"/> condemn the current rung. Two, not
    /// one: a single network hiccup must not cost the user quality for the rest of the session, and the
    /// ticket's last risk is precisely a rule that degrades streams which were working.
    /// </summary>
    public const int StarvationsBeforeStepDown = 2;

    /// <summary>
    /// How long a rung that has never failed must play clean before the next one up is tried.
    /// <para>Was sixty seconds, and the owner's own eleven-minute session said that was far too eager:
    /// ten legs, <b>zero</b> reconnects - every interruption was a quality re-open - and 108.9 s of black
    /// screen out of 656 s, 73 % of it spent probing. A probe costs a re-open whether it succeeds or
    /// fails, so it has to be a rare event rather than a rhythm.</para>
    /// </summary>
    public static readonly TimeSpan FirstProbeAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The ceiling on a rung's own wait. A rung that has refused four times is effectively closed for a
    /// normal viewing session - which is the intended end state, reached by evidence rather than by
    /// decree: the rung is never permanently banned, it just has to be worth the interruption.
    /// </summary>
    public static readonly TimeSpan MaximumProbeWait = TimeSpan.FromHours(1);

    private readonly List<TimeSpan> _starvations = [];
    private IReadOnlyList<StreamQualityRung> _ladder = [];
    /// <summary>
    /// How many times each rung has failed a probe, indexed like the ladder. The wait belongs to the
    /// <em>rung</em>, not to the governor, and that distinction is the whole of the field defect: with one
    /// shared wait, a probe that succeeded at 796k reset it to the base and sent the player straight back
    /// to a top rung it had already failed three times. A success at one rung says nothing about another.
    /// </summary>
    private int[] _failures = [];
    private int _index;
    private TimeSpan _cleanSince;
    private TimeSpan? _probedAt;

    /// <summary>
    /// Adopts the ladder read from the source, ascending by bandwidth. Fewer than two rungs disables the
    /// governor for good - there is nothing to choose between - and every input then returns no decision.
    /// </summary>
    /// <param name="recalledFailures">
    /// What earlier sessions recorded about this source, by rung bandwidth (see <see cref="QualityMemory"/>),
    /// or null for no evidence. It seeds the probe waits and nothing else. Bandwidth, not ladder position,
    /// because a source that re-encodes would otherwise have one rung's record applied to another.
    /// </param>
    /// <param name="openedCeiling">
    /// SP-0076: the restriction the currently playing media was actually opened with, or null when it was
    /// opened unrestricted. This is not a preference and not a second opinion - it is what the engine was
    /// told, so the governor has to enter the ladder where the engine already is or every reading it takes
    /// afterwards would be attributed to the wrong rendition.
    /// <para>The rung chosen is the highest one that fits inside the cap, which is the rule libvlc's own
    /// representation selector applies, so the two models agree by construction. A cap that fits no rung at
    /// all describes a ladder this source no longer offers: the session then adopts the top rung and says
    /// <see cref="QualityLadderEntry.Missed"/>, which is today's behaviour plus a log line.</para>
    /// </param>
    public void UseLadder(
        IReadOnlyList<StreamQualityRung> rungs,
        TimeSpan now,
        IReadOnlyDictionary<int, int>? recalledFailures = null,
        StreamQualityRung? openedCeiling = null)
    {
        _ladder = rungs.Count >= 2 ? rungs : [];
        (_index, LadderEntry) = EnterAt(openedCeiling);
        _failures = new int[_ladder.Count];
        if (recalledFailures is not null)
        {
            for (var rung = 0; rung < _ladder.Count; rung++)
            {
                if (recalledFailures.TryGetValue(_ladder[rung].BandwidthBps, out var failures) && failures > 0)
                {
                    _failures[rung] = failures;
                }
            }
        }

        _probedAt = null;
        MemoryRevision = 0;
        StartCleanInterval(now);
    }

    /// <summary>SP-0076: where this session entered the ladder - for the log, which is the only place a
    /// restriction the user never asked for can be read back.</summary>
    public QualityLadderEntry LadderEntry { get; private set; }

    /// <summary>The ladder position to start from, and the entry it counts as.</summary>
    private (int Index, QualityLadderEntry Entry) EnterAt(StreamQualityRung? openedCeiling)
    {
        var top = _ladder.Count - 1;
        if (openedCeiling is not { } ceiling || _ladder.Count == 0)
        {
            return (top, QualityLadderEntry.Top);
        }

        for (var rung = top; rung >= 0; rung--)
        {
            if (_ladder[rung].Width <= ceiling.Width && _ladder[rung].Height <= ceiling.Height)
            {
                return (rung, rung == top ? QualityLadderEntry.Top : QualityLadderEntry.Remembered);
            }
        }

        return (top, QualityLadderEntry.Missed);
    }

    /// <summary>
    /// Bumped every time a rung's record changes, so the player can persist without polling or a callback.
    /// It exists because one of the two changes is invisible in the return values: a probe that survives
    /// its window clears that rung's failures from inside <see cref="Observe"/>, which returns null.
    /// </summary>
    public int MemoryRevision { get; private set; }

    /// <summary>
    /// The rungs with something on their record, for <see cref="QualityMemory"/> to persist. Only failures:
    /// a rung at zero is the default any fresh ladder starts from, and writing it down would say "known
    /// good" where the truth is "nothing observed".
    /// </summary>
    public IReadOnlyList<QualityRungMemory> Failures
    {
        get
        {
            if (!HasLadder)
            {
                return [];
            }

            var recorded = new List<QualityRungMemory>();
            for (var rung = 0; rung < _ladder.Count; rung++)
            {
                if (_failures[rung] > 0)
                {
                    recorded.Add(new QualityRungMemory(_ladder[rung].BandwidthBps, _failures[rung]));
                }
            }

            return recorded;
        }
    }

    /// <summary>True once a usable ladder is known; until then no decision can be taken.</summary>
    public bool HasLadder => _ladder.Count >= 2;

    /// <summary>
    /// The ceiling to open the next media with, or null for "whatever the engine would have chosen".
    /// Null on the top rung as well as with no ladder: capping at the highest rendition would be a
    /// restriction that restricts nothing, and the log should say <c>none</c> rather than imply a limit.
    /// </summary>
    public StreamQualityRung? Ceiling => HasLadder && _index < _ladder.Count - 1 ? _ladder[_index] : null;

    /// <summary>
    /// The rung currently chosen, including the top one. Distinct from <see cref="Ceiling"/> on purpose:
    /// the ceiling answers "what is applied to the engine" and is null at the top, while this answers
    /// "what are we on" - and a log that said <c>from=none</c> would name no rung at all.
    /// </summary>
    public StreamQualityRung? CurrentRung => HasLadder ? _ladder[_index] : null;

    /// <summary>True when there is nothing left to step down to, so the App can say so once.</summary>
    public bool AtLowestRung => HasLadder && _index == 0;

    /// <summary>
    /// The stream ran out of buffer while live - a stall or an SP-0070 caught freeze. Returns the rung to
    /// move to once the pattern is established, or null while it is not.
    /// </summary>
    public QualityDecision? NotifyStarvation(TimeSpan now)
    {
        if (!HasLadder)
        {
            return null;
        }

        _starvations.RemoveAll(at => now - at >= StarvationWindow);
        _starvations.Add(now);
        if (_starvations.Count < StarvationsBeforeStepDown || _index == 0)
        {
            return null;
        }

        var starvations = _starvations.Count;
        if (_probedAt is not null)
        {
            // This rung was on trial and failed it. The failure is recorded against *this* rung, so the
            // next attempt at it waits twice as long however well any other rung behaves meanwhile.
            _probedAt = null;
            _failures[_index]++;
            MemoryRevision++;
        }

        _index--;
        StartCleanInterval(now);
        return new QualityDecision(_ladder[_index], QualityChangeKind.StepDown, starvations);
    }

    /// <summary>
    /// One observation on the player's existing sampling cadence. This is where a probe on trial is
    /// declared a success, and where the next probe is raised.
    /// </summary>
    public QualityDecision? Observe(TimeSpan now)
    {
        if (!HasLadder)
        {
            return null;
        }

        // A probe has succeeded exactly when it has survived the window that would have condemned it;
        // there is deliberately no separate grace constant for two ways of saying the same thing.
        // Its own past is forgiven - and only its own: a rung that proves itself says nothing about the
        // one above, which keeps whatever it earned.
        if (_probedAt is { } probed && now - probed >= StarvationWindow)
        {
            _probedAt = null;
            if (_failures[_index] > 0)
            {
                _failures[_index] = 0;
                MemoryRevision++;
            }
        }

        if (_index >= _ladder.Count - 1 || now - _cleanSince < WaitBeforeProbing(_index + 1))
        {
            return null;
        }

        _index++;
        _probedAt = now;
        StartCleanInterval(now);
        return new QualityDecision(_ladder[_index], QualityChangeKind.Probe, 0);
    }

    /// <summary>
    /// How long the current rung must play clean before <paramref name="rung"/> is worth interrupting it
    /// for: the base wait doubled once per recorded failure of that rung, capped.
    /// </summary>
    /// <remarks>Doubled by addition rather than by shifting, so a long-lived session cannot overflow it.</remarks>
    private TimeSpan WaitBeforeProbing(int rung)
    {
        var wait = FirstProbeAfter;
        for (var failure = 0; failure < _failures[rung] && wait < MaximumProbeWait; failure++)
        {
            wait += wait;
        }

        return wait < MaximumProbeWait ? wait : MaximumProbeWait;
    }

    /// <summary>
    /// Every rung change restarts the interval and drops the starvations that argued for it. Without
    /// this the evidence against the old rung would still be counted against the new one, and a single
    /// bad minute would walk the ladder to the bottom in one go.
    /// </summary>
    private void StartCleanInterval(TimeSpan now)
    {
        _cleanSince = now;
        _starvations.Clear();
    }
}
