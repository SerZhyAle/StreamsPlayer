using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>What the launching session found where the finished session's log used to be (SP-0085).</summary>
public enum LogRotationOutcome
{
    /// <summary>
    /// Nothing was there, or it was retired: <see cref="DiagnosticLogFiles.CurrentLogName"/> is free for
    /// the launching session to take.
    /// </summary>
    CurrentLogFree,

    /// <summary>
    /// The finished session's log could not be retired and still holds
    /// <see cref="DiagnosticLogFiles.CurrentLogName"/> - normally because a previous instance has not let
    /// go of the handle yet. The launching session must write somewhere else or it writes nowhere.
    /// </summary>
    PreviousLogRetained,
}

/// <summary>
/// Names and retention rule for the session diagnostic logs (SP-0013, SP-0040, SP-0055).
/// </summary>
/// <remarks>
/// SP-0013 replaced the log on every launch. SP-0040 kept one extra generation, because the log worth
/// mailing to the author is almost always the previous session's: a user whose player misbehaved,
/// closed it and then went looking for the send button would otherwise have destroyed the evidence on
/// the way there. SP-0055 raises that to <see cref="KeptSessions"/> launches in total, because two
/// generations still lose a report the moment the user restarts twice - which is exactly what someone
/// does when the player misbehaves.
/// <para>Retention is a flat count of launches by explicit decision: a run that logged nothing still
/// consumes a slot. That is predictable, and predictability is what a user needs when told "the last
/// ten runs are kept". The cost is that a burst of quick restarts, or a crash loop, evicts real
/// evidence - so a report is still worth collecting promptly.</para>
/// </remarks>
public static class DiagnosticLogFiles
{
    public const string CurrentLogName = "Current.log";

    /// <summary>
    /// How many sessions are kept in total, <em>including</em> the one currently running. Ten launches
    /// are retained, so the eleventh launch is the first that deletes anything - which is also exactly
    /// what the interface copy promises the user.
    /// </summary>
    public const int KeptSessions = 10;

    /// <summary>Closed sessions kept beside the live <see cref="CurrentLogName"/>.</summary>
    private const int KeptRetiredSessions = KeptSessions - 1;

    /// <summary>The single spare log SP-0040 kept; adopted into the new naming on first launch.</summary>
    private const string LegacyPreviousLogName = "Previous.log";

    private const string SessionPrefix = "Session-";
    private const string LogExtension = ".log";
    private const string StampFormat = "yyyyMMdd-HHmm";
    private const string SessionSearchPattern = SessionPrefix + "*" + LogExtension;

    /// <summary>
    /// Retires the finished session's log under a name carrying when that session started, then prunes
    /// the archive to the newest <see cref="KeptSessions"/>, and reports whether
    /// <see cref="CurrentLogName"/> came free.
    /// <para>Best-effort by contract: a locked or ACL-denied file costs retention, never the launch.
    /// The caller still needs a writable current log to start, and that is the only hard requirement.</para>
    /// <para>SP-0085: the returned outcome is that requirement's other half. Retirement failing silently
    /// left the caller opening a name another process still owned, which throws - and cost a whole
    /// session its log. A caller told the name is taken can write somewhere else instead.</para>
    /// </summary>
    public static LogRotationOutcome Rotate(string directory)
    {
        AdoptLegacyPreviousLog(directory);
        var current = Path.Combine(directory, CurrentLogName);
        var outcome = LogRotationOutcome.CurrentLogFree;
        try
        {
            if (File.Exists(current))
            {
                File.Move(current, ReserveSessionPath(directory, ReadSessionStart(current)));
            }
        }
        catch (Exception exception) when (IsRetentionFailure(exception))
        {
            // Keep going: pruning is still worth attempting even if this session could not be retired.
            outcome = LogRotationOutcome.PreviousLogRetained;
        }

        Prune(directory);
        return outcome;
    }

    /// <summary>
    /// Brings the single <c>Previous.log</c> written by SP-0040 builds into the new naming, once, on the
    /// first launch after the upgrade. Without this the file is invisible to both halves of the feature:
    /// never packed into a report and never pruned, so a user upgrading mid-problem loses exactly the
    /// session they were about to send and keeps a file nothing will ever clean up.
    /// </summary>
    private static void AdoptLegacyPreviousLog(string directory)
    {
        var legacy = Path.Combine(directory, LegacyPreviousLogName);
        try
        {
            if (File.Exists(legacy))
            {
                // Its own start time is unknowable now, so use the last write - when that session ended.
                File.Move(legacy, ReserveSessionPath(directory, File.GetLastWriteTime(legacy)));
            }
        }
        catch (Exception exception) when (IsRetentionFailure(exception))
        {
            // An unreadable leftover costs one old session, never the launch.
        }
    }

    /// <summary>The session logs that exist right now, current first then newest-retired - the archive's entries.</summary>
    public static IReadOnlyList<string> ExistingLogs(string directory)
    {
        List<string> logs = [];
        var current = Path.Combine(directory, CurrentLogName);
        if (File.Exists(current))
        {
            logs.Add(current);
        }

        logs.AddRange(RetiredNewestFirst(directory));
        return logs;
    }

    private static void Prune(string directory)
    {
        try
        {
            foreach (var stale in RetiredNewestFirst(directory).Skip(KeptRetiredSessions))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception exception) when (IsRetentionFailure(exception))
                {
                    // One undeletable file must not stop the rest of the prune.
                }
            }
        }
        catch (Exception exception) when (IsRetentionFailure(exception))
        {
            // The directory became unreadable; the current log still opens.
        }
    }

    private static List<string> RetiredNewestFirst(string directory)
    {
        try
        {
            // Ordered by write time rather than by parsing the name: a retired log is never written to
            // again, so its write time is a faithful and total order even for a name we cannot parse.
            return new DirectoryInfo(directory)
                .EnumerateFiles(SessionSearchPattern)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToList();
        }
        catch (Exception exception) when (IsRetentionFailure(exception))
        {
            return [];
        }
    }

    /// <summary>
    /// Builds a free path for a session log started at <paramref name="startedLocal"/>. Two sessions can
    /// start within the same minute - a quick restart, or an automated run - so a taken name gets a
    /// numeric suffix rather than overwriting the evidence the rotation exists to preserve.
    /// <para>Public because retirement is not its only caller: a session that <see cref="Rotate"/> told
    /// <see cref="LogRotationOutcome.PreviousLogRetained"/> writes here from the start, under its own
    /// start time (SP-0085). Such a log needs no later retirement - it is already named as one - and it
    /// is found, packed and pruned by the same rules as every other session's.</para>
    /// </summary>
    public static string ReserveSessionPath(string directory, DateTime startedLocal)
    {
        var stamp = startedLocal.ToString(StampFormat, CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{SessionPrefix}{stamp}{LogExtension}");
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{SessionPrefix}{stamp}-{suffix}{LogExtension}");
        }

        return candidate;
    }

    /// <summary>
    /// When the finished session started, in local time - the same clock the version stamp uses, so a
    /// file name can be matched against "when did this go wrong" without converting time zones.
    /// <para>Taken from the log's own first record, which the writer stamps at startup, because file
    /// creation time is unreliable here: NTFS tunnelling hands a recreated <c>Current.log</c> the
    /// creation time of the one it replaced when the two are seconds apart. Falls back to the last
    /// write - the session's end - when the first line is missing or unreadable.</para>
    /// </summary>
    private static DateTime ReadSessionStart(string path)
    {
        try
        {
            var firstLine = File.ReadLines(path).FirstOrDefault();
            var stamp = firstLine?.Split(' ', 2)[0];
            if (stamp is not null &&
                DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
            {
                return started.ToLocalTime().DateTime;
            }
        }
        catch (Exception exception) when (IsRetentionFailure(exception))
        {
            // Fall through to the file's own timestamp.
        }

        return File.GetLastWriteTime(path);
    }

    private static bool IsRetentionFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
