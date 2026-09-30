using System.Globalization;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>The kinds of file this product writes for the user, as CAPTURE-OUTPUT rule 1 names them.</summary>
public enum CaptureKind
{
    /// <summary>A still taken from the playing picture - <c>video_frame</c>, role <c>frames</c>.</summary>
    VideoFrame,

    /// <summary>A recording of a live video stream - <c>stream_video</c>, role <c>stream-recordings</c>.</summary>
    StreamVideo,

    /// <summary>A recording of a radio station - <c>stream_audio</c>, role <c>stream-recordings</c>.</summary>
    StreamAudio
}

/// <summary>
/// SP-0179: the automatic name of a captured file, CAPTURE-OUTPUT rules 3-5 -
/// <c>&lt;prefix&gt;_&lt;yyMMdd&gt;_&lt;HHmmss&gt;[_&lt;label&gt;][ (&lt;n&gt;)].&lt;ext&gt;</c>. The label is the channel
/// the capture came from. Platform-neutral so the grammar is held by tests in any interface language.
/// </summary>
public static class CaptureFileName
{
    /// <summary>The capture's start in local wall-clock time, formatted invariant (rule 3).</summary>
    public const string TimestampFormat = "yyMMdd_HHmmss";

    /// <summary>The longest label kept, in UTF-16 code units (rule 3).</summary>
    public const int MaxLabelLength = 80;

    /// <summary>A frame is JPEG (rule 1); the product offers no PNG choice.</summary>
    public const string FrameExtension = ".jpg";

    /// <summary>The container a video recording takes when the engine does not say (rule 13: the source's own).</summary>
    public const string DefaultVideoExtension = ".mp4";

    /// <summary>How many ordinals are tried before a folder is declared full of this one name.</summary>
    public const int MaxOrdinal = 100;

    /// <summary>Rule 7's character set: what no file system accepts, besides control characters.</summary>
    private const string ForbiddenCharacters = "\\/:*?\"<>|";

    public static string Prefix(CaptureKind kind) => kind switch
    {
        CaptureKind.VideoFrame => "video_frame",
        CaptureKind.StreamVideo => "stream_video",
        CaptureKind.StreamAudio => "stream_audio",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>
    /// The automatic name for a capture of <paramref name="kind"/> that started at <paramref name="startedAt"/>.
    /// <paramref name="source"/> is the raw channel title, cleaned into the label here; <paramref name="extension"/>
    /// is the container the file actually has, or null for the kind's own default.
    /// </summary>
    public static string For(CaptureKind kind, DateTimeOffset startedAt, string? source, string? extension = null)
    {
        var builder = new StringBuilder(Prefix(kind))
            .Append('_')
            .Append(startedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        if (Label(source) is { } label)
        {
            builder.Append('_').Append(label);
        }

        return builder.Append(NormalizeExtension(kind, extension)).ToString();
    }

    /// <summary>
    /// The label for <paramref name="source"/>, or null when nothing usable is left: rule 7's characters and
    /// control characters become <c>_</c>, outer spaces and trailing dots go, and the result is cut to
    /// <see cref="MaxLabelLength"/> without splitting a character.
    /// </summary>
    public static string? Label(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            builder.Append(char.IsControl(character) || ForbiddenCharacters.Contains(character) ? '_' : character);
        }

        var label = Trim(builder.ToString());
        if (label.Length > MaxLabelLength)
        {
            // Trimmed again: the cut can leave a space or a dot at the new end.
            label = Trim(TextBoundary.Truncate(label, MaxLabelLength));
        }

        return label.Length == 0 ? null : label;
    }

    /// <summary><c>&lt;stem&gt; (&lt;n&gt;).&lt;ext&gt;</c> - the rule 5 form of the <paramref name="ordinal"/>-th file of one name.</summary>
    public static string WithOrdinal(string fileName, int ordinal)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 2);
        var dot = fileName.LastIndexOf('.');
        return dot <= 0
            ? $"{fileName} ({ordinal})"
            : $"{fileName[..dot]} ({ordinal}){fileName[dot..]}";
    }

    /// <summary>
    /// The first of <paramref name="fileName"/>, <c>(2)</c>, <c>(3)</c> .. that <paramref name="taken"/> says is
    /// free in the destination (rules 5-6), or null when <see cref="MaxOrdinal"/> of them are taken.
    /// </summary>
    public static string? FirstFree(string fileName, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken(fileName))
        {
            return fileName;
        }

        for (var ordinal = 2; ordinal <= MaxOrdinal; ordinal++)
        {
            var candidate = WithOrdinal(fileName, ordinal);
            if (!taken(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Trim(string text) => text.Trim(' ').TrimEnd('.', ' ');

    private static string NormalizeExtension(CaptureKind kind, string? extension)
    {
        if (kind == CaptureKind.VideoFrame)
        {
            return FrameExtension;
        }

        var trimmed = extension?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == ".")
        {
            // A radio recording is named only once its bytes have said what it is (SP-0121); there is no
            // default to fall back on, and guessing one is the mislabelling that ticket removed.
            return kind == CaptureKind.StreamVideo
                ? DefaultVideoExtension
                : throw new ArgumentException("A stream audio recording needs the extension of the audio it holds.", nameof(extension));
        }

        return (trimmed.StartsWith('.') ? trimmed : "." + trimmed).ToLowerInvariant();
    }
}
