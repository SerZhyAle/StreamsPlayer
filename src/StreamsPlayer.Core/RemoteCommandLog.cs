namespace StreamsPlayer.Core;

/// <summary>
/// STRWIN-0303: one line of the target's log (<c>REMOTE-CONTROL</c> rule 11): the time, the controller's name and id, the
/// verb, its class, the outcome and the exit code, with every argument value redacted. <see cref="Verified"/> is false for
/// a request that did not open: its <see cref="ControllerId"/> is then the frame's clear routing id, unverified.
/// </summary>
public sealed record RemoteCommandLogEntry(
    DateTimeOffset At,
    string ControllerName,
    string ControllerId,
    bool Verified,
    string Verb,
    string RiskClass,
    string Outcome,
    int Exit,
    IReadOnlyList<string> Arguments);

/// <summary>STRWIN-0303: the last entries of the target's log, kept on this device only and never sent anywhere.</summary>
public sealed class RemoteCommandLog
{
    public const int MaximumEntries = 500;

    /// <summary>The outcome of a box that did not open (item DR, rule 4).</summary>
    public const string SealedFailed = "sealed-failed";

    /// <summary>The outcome of a box the ring had already answered (rule 12): answered again, never run again.</summary>
    public const string Repeated = "repeated";

    private const string Redacted = "<redacted>";

    private readonly List<RemoteCommandLogEntry> _entries;

    public RemoteCommandLog(IEnumerable<RemoteCommandLogEntry>? entries = null)
    {
        _entries = (entries ?? []).Where(entry => entry is { Verb: not null, ControllerId: not null }).ToList();
        Trim();
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<RemoteCommandLogEntry> Entries => _entries;

    /// <summary>The newest <paramref name="count"/> entries, newest first - what the target shows beside the switch.</summary>
    public IReadOnlyList<RemoteCommandLogEntry> Latest(int count) =>
        _entries.AsEnumerable().Reverse().Take(Math.Max(0, count)).ToArray();

    public void Add(RemoteCommandLogEntry entry)
    {
        _entries.Add(entry);
        Trim();
    }

    /// <summary>
    /// The arguments as the log keeps them (<c>CLI-EVENT-STREAM</c> rule 13 through <c>APP-COMMANDS</c> rule 16): the
    /// option names a request carried, each value replaced, so neither an address nor a credential is ever written.
    /// </summary>
    public static IReadOnlyList<string> RedactedArguments(IEnumerable<string> optionNames) =>
        optionNames.Select(name => $"--{name}={Redacted}").ToArray();

    private void Trim()
    {
        if (_entries.Count > MaximumEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaximumEntries);
        }
    }
}
