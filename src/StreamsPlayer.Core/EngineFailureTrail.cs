namespace StreamsPlayer.Core;

/// <summary>
/// SP-0189: what a media engine said, in its own error lines, about a connection it could not open - kept so the
/// failure is logged with its cause instead of with the name of a wrapper. Plain strings in, one bounded line out.
/// </summary>
/// <remarks>
/// <para>
/// Evidence (LibVLC 3.0.23 with the radio engine's own options, SP-0189 research): an open that fails logs its
/// cause at error level first - "connection failed: Connection refused by peer", "cannot resolve &lt;host&gt; port
/// 443: No such host is known.", "HTTP 404 error" - then two generic lines, "Your input can't be opened" and "VLC is
/// unable to open the MRL '&lt;address&gt;'", and only then raises its error event. The generic pair names no cause
/// and the second carries the whole address, so both are dropped. The audio-output layer logs its errors on its own
/// schedule, for plays that succeed as well, and an output that cannot start raises no error event at all - those
/// lines say nothing about why an open failed, so they are dropped too.
/// </para>
/// <para>
/// The text is for the log only and is never classified: an engine line names the host, and a host called
/// "codec-fm" must not turn a refused connection into a hard failure. Not thread-safe by design, matching
/// <see cref="EngineLogNoiseFilter"/>: engine log callbacks arrive on engine threads, and the engine that owns
/// this trail owns the lock.
/// </para>
/// </remarks>
public sealed class EngineFailureTrail
{
    /// <summary>How many distinct lines are kept: the most recent ones, which are the ones nearest the failure.</summary>
    public const int MaximumLines = 4;

    /// <summary>Longest rendered cause, so a runaway native string cannot flood the log line.</summary>
    public const int MaximumLength = 300;

    private const string Separator = "; ";

    // Written after every failed open, whatever its cause.
    private static readonly string[] GenericOpenFailures = ["Your input can't be opened", "VLC is unable to open the MRL"];

    // The engine's audio outputs on Windows.
    private static readonly string[] AudioOutputModules = ["mmdevice", "wasapi", "directsound", "waveout"];

    private readonly List<string> _lines = [];

    /// <summary>Forgets every line; called as a new connection opens, so one connection's words never explain another's failure.</summary>
    public void Clear() => _lines.Clear();

    /// <summary>Records one error-level engine line, unless it is one that never explains a failed open.</summary>
    public void Observe(string? module, string? message)
    {
        var line = Flatten(message);
        if (line is null || IsNoise(module, line))
        {
            return;
        }

        // A repeated line moves to the end instead of taking a second slot: the engine retries a connect and
        // says the same thing again, and the distinct lines are what describe the failure.
        _lines.Remove(line);
        _lines.Add(line);
        if (_lines.Count > MaximumLines)
        {
            _lines.RemoveAt(0);
        }
    }

    /// <summary>The kept lines, oldest first, as one bounded line; null when the engine said nothing that explains a failure.</summary>
    public string? Describe() => _lines.Count == 0 ? null : Bound(string.Join(Separator, _lines));

    // One event is one log line: a line break inside a value would start a fake entry.
    private static string? Flatten(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Bound(text.ReplaceLineEndings(" ").Trim());

    private static string Bound(string text) => text.Length <= MaximumLength ? text : text[..MaximumLength];

    private static bool IsNoise(string? module, string line)
    {
        foreach (var generic in GenericOpenFailures)
        {
            if (line.StartsWith(generic, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var output in AudioOutputModules)
        {
            if (string.Equals(module, output, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // The core's own lines about an output that would not start; the second names no module at all.
        return line.Contains("audio output", StringComparison.OrdinalIgnoreCase)
            || line.Equals("module not functional", StringComparison.OrdinalIgnoreCase);
    }
}
