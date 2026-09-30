using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0137, <c>DIAGNOSTIC-REPORT</c> rule 3 (path half): an absolute path into the user's profile leaves the
/// machine with the personal directory replaced by <see cref="UserToken"/> or <see cref="AppDataToken"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the root is replaced; the rest of the path stays, because the path is the diagnosis -
/// <c>&lt;USER&gt;\Music\x.mp3</c> still says where the file was. Roots are tried longest first, so the data
/// directory - itself inside the profile - reads <c>&lt;APP_DATA&gt;\Current.log</c> rather than
/// <c>&lt;USER&gt;\AppData\Local\StreamsPlayer\Current.log</c>.
/// </para>
/// <para>
/// Matching ignores case and accepts either separator, so a path quoted inside a <c>file:///C:/Users/..</c>
/// address is caught as well. A known root matches only up to a segment boundary: <c>C:\Users\ann</c> does not
/// claim <c>C:\Users\anna</c>. After the known roots, any remaining <c>X:\Users\&lt;name&gt;</c> is replaced
/// too - an 8.3 short name (<c>C:\Users\JOHNSM~1</c>, which is how the temp directory often appears), another
/// account's profile, or a log kept from before the profile was renamed. That fallback stops the name at
/// whitespace, so a profile name with a space that is not the running user's keeps its second word; the
/// running user's own profile is matched exactly, spaces included.
/// </para>
/// </remarks>
public sealed class DiagnosticPathRedactor
{
    public const string UserToken = "<USER>";
    public const string AppDataToken = "<APP_DATA>";

    /// <summary>What a line whose redaction could not finish in time is replaced with (SP-0174).</summary>
    public const string TimeoutMarker = "[Diag] PATH REDACTION TIMEOUT | dropped_line_bytes=";

    internal static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A path may not start mid-word: <c>xC:\Users</c> is not a path.</summary>
    private const string PathStart = @"(?<![\w.])";

    /// <summary>
    /// A known root may end only where a path segment ends: at a separator, whitespace, a quote, a bracket or
    /// punctuation that cannot continue a directory name, or the end of the text.
    /// </summary>
    private const string SegmentEnd = @"(?=$|[\\/\s""'<>|:;,)\]}])";

    private static readonly Regex AnyProfilePattern = new(
        PathStart + @"[A-Za-z]:[\\/]+Users[\\/]+[^\\/:*?""<>|\s]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Regex _anyProfile;

    private readonly Regex? _knownRoots;
    private readonly string[] _tokens;

    /// <param name="appDataDirectory">This product's data directory, replaced with <see cref="AppDataToken"/>.</param>
    /// <param name="userProfileDirectory">The user's profile directory, replaced with <see cref="UserToken"/>.</param>
    public DiagnosticPathRedactor(string? appDataDirectory, string? userProfileDirectory)
        : this(appDataDirectory, userProfileDirectory, DefaultMatchTimeout)
    {
    }

    /// <param name="matchTimeout">Per line. Tests inject <see cref="TimeSpan.Zero"/> to force the timeout path.</param>
    internal DiagnosticPathRedactor(string? appDataDirectory, string? userProfileDirectory, TimeSpan matchTimeout)
    {
        _anyProfile = new Regex(AnyProfilePattern.ToString(), AnyProfilePattern.Options, matchTimeout);
        var roots = new List<(string Key, string Pattern, string Token)>();
        AddRoot(roots, appDataDirectory, AppDataToken);
        AddRoot(roots, userProfileDirectory, UserToken);

        // Alternation tries left to right, so the longest root has to come first.
        var ordered = roots.OrderByDescending(root => root.Key.Length).ToArray();
        _tokens = ordered.Select(root => root.Token).ToArray();
        if (ordered.Length > 0)
        {
            var alternatives = ordered.Select((root, index) => $"(?<r{index}>{root.Pattern})");
            _knownRoots = new Regex(
                PathStart + "(?:" + string.Join('|', alternatives) + ")" + SegmentEnd,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                matchTimeout);
        }
    }

    /// <summary>The redactor for the running user, with <paramref name="appDataDirectory"/> as the data directory.</summary>
    public static DiagnosticPathRedactor ForCurrentUser(string? appDataDirectory) =>
        new(appDataDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Replaces every profile or data-directory root in <paramref name="text"/>; never throws.
    /// </summary>
    /// <remarks>
    /// SP-0174: redaction runs per line and the timeout budget restarts with each line, so a line
    /// pathological enough to time out costs only itself - it becomes <see cref="TimeoutMarker"/> plus its
    /// length, and every other line of the chunk reaches the archive intact. The previous whole-chunk
    /// replacement threw away the log body the archive was built to carry, and did so non-deterministically
    /// under the load of the full test run.
    /// </remarks>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (!text.Contains('\n'))
        {
            return RedactLine(text);
        }

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = RedactLine(lines[index]);
        }

        return string.Join('\n', lines);
    }

    private string RedactLine(string line)
    {
        // Neither regex can match without one of these characters: every known root spans a path
        // separator, and any profile root starts at a drive colon. A line without them skips the
        // regexes entirely - which is what keeps a multi-megabyte measurement line from spending its
        // whole timeout budget on a scan that cannot succeed (SP-0174, G-01).
        if (line.AsSpan().IndexOfAny(':', '\\', '/') < 0)
        {
            return line;
        }

        try
        {
            var redacted = _knownRoots is null ? line : _knownRoots.Replace(line, TokenFor);
            return _anyProfile.Replace(redacted, UserToken);
        }
        catch (RegexMatchTimeoutException)
        {
            // A line this hostile is not worth shipping, but its neighbours are: replace only this
            // line. The CR of a CRLF pair travels with the line; keep it, or the marker glues two log
            // lines into one.
            var cr = line.EndsWith('\r') ? "\r" : string.Empty;
            return TimeoutMarker + line[..^cr.Length].Length.ToString(CultureInfo.InvariantCulture) + cr;
        }
    }

    private string TokenFor(Match match)
    {
        for (var index = 0; index < _tokens.Length; index++)
        {
            if (match.Groups[$"r{index}"].Success)
            {
                return _tokens[index];
            }
        }

        return UserToken;
    }

    private static void AddRoot(List<(string Key, string Pattern, string Token)> roots, string? directory, string token)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var segments = directory.Trim().Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        // A drive or share root alone ("C:\", "\\server") would claim every path on it.
        if (segments.Length < 2)
        {
            return;
        }

        var key = string.Join('\\', segments);
        if (roots.Any(root => string.Equals(root.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var unc = directory.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) ||
                  directory.TrimStart().StartsWith("//", StringComparison.Ordinal);
        roots.Add((key, (unc ? @"[\\/]{2}" : string.Empty) + string.Join(@"[\\/]+", segments.Select(Regex.Escape)), token));
    }
}
