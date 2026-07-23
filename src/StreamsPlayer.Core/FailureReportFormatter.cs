using System.Globalization;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Inputs for a shareable playback-failure report. The caller injects the app version and timestamp so the
/// formatter stays platform-neutral and deterministic (no ambient clock or assembly access in Core).
/// </summary>
public sealed record FailureReport(
    string AppVersion,
    DateTimeOffset TimestampUtc,
    string ChannelTitle,
    string Url,
    MediaKind MediaKind,
    PlaybackErrorCategory Category);

/// <summary>
/// Renders a bounded, human-readable failure report for deliberate copy/paste. Contains only the fields the
/// contract allows (version, UTC time, title, media kind, error category, redacted URL) and never local
/// paths, credentials, logs, or catalog contents.
/// </summary>
public static class FailureReportFormatter
{
    public static string Format(FailureReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("StreamsPlayer stream report");
        builder.Append("Version: ").AppendLine(report.AppVersion);
        builder.Append("Time (UTC): ").AppendLine(report.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        builder.Append("Title: ").AppendLine(report.ChannelTitle?.Trim() ?? string.Empty);
        builder.Append("Kind: ").AppendLine(report.MediaKind.ToString());
        builder.Append("Error: ").AppendLine(report.Category.ToString());
        builder.Append("URL: ").AppendLine(CatalogUrlIdentity.Redact(report.Url));
        return builder.ToString().TrimEnd();
    }
}
