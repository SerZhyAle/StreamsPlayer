namespace StreamsPlayer.Core;

/// <summary>
/// Names and retention rule for the session diagnostic logs (SP-0013, extended by SP-0040).
/// </summary>
/// <remarks>
/// SP-0013 replaced the log on every launch. SP-0040 keeps one generation, because the log worth
/// mailing to the author is almost always the previous session's: a user whose player misbehaved,
/// closed it and then went looking for the send button would otherwise have destroyed the evidence
/// on the way there. Two generations is the smallest retention that makes that report possible.
/// </remarks>
public static class DiagnosticLogFiles
{
    public const string CurrentLogName = "Current.log";
    public const string PreviousLogName = "Previous.log";

    /// <summary>
    /// Moves an existing current-session log onto the previous-session name, replacing the older one.
    /// Best-effort by contract: a locked or ACL-denied file costs the previous log, never the launch.
    /// </summary>
    public static void RotateCurrentToPrevious(string directory)
    {
        var current = Path.Combine(directory, CurrentLogName);
        try
        {
            if (File.Exists(current))
            {
                File.Move(current, Path.Combine(directory, PreviousLogName), overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Retention is a convenience; the caller still needs a writable current log to start.
        }
    }

    /// <summary>The session logs that exist right now, current first - the archive's log entries.</summary>
    public static IReadOnlyList<string> ExistingLogs(string directory)
    {
        List<string> logs = [];
        foreach (var name in new[] { CurrentLogName, PreviousLogName })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                logs.Add(path);
            }
        }

        return logs;
    }
}
