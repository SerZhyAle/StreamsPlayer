using System.Globalization;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// The facts that travel with a user-sent log archive (SP-0040): version, platform, selected
/// settings, and catalog volume - the questions the author otherwise has to ask before reading
/// the log at all.
/// </summary>
/// <remarks>
/// Deliberately count-only. No channel title, no URL, no collection name, no history entry: the
/// user is mailing this to a person, and the archive's own contract (SP-0040 criterion 3) is that
/// the summary identifies the installation's shape, not its content.
/// </remarks>
public sealed record DiagnosticEnvironment(
    string AppVersion,
    string OperatingSystem,
    string Architecture,
    AppLanguage? InterfaceLanguage,
    MediaBackend MediaBackend,
    int SchemaVersion,
    int TotalChannels,
    int CatalogChannels,
    int LocalCatalogChannels,
    int ManualChannels,
    int ImportedChannels,
    int PinnedChannels,
    int HiddenChannels,
    int Collections,
    int HistoryEntries,
    DateTimeOffset? CatalogRefreshedUtc,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<KeyValuePair<string, string>>? Extras = null);

public static class DiagnosticEnvironmentSummary
{
    /// <summary>The token used when the user has never chosen an interface language.</summary>
    public const string LanguageNotChosen = "not_chosen";

    public static DiagnosticEnvironment From(
        CatalogState state,
        string appVersion,
        string operatingSystem,
        string architecture,
        DateTimeOffset generatedUtc,
        IReadOnlyList<KeyValuePair<string, string>>? extras = null) =>
        new(
            appVersion,
            operatingSystem,
            architecture,
            state.Language,
            state.VideoBackend,
            state.SchemaVersion,
            state.Channels.Count,
            state.Channels.Count(channel => channel.SourceOrigin == SourceOrigin.Catalog),
            state.Channels.Count(channel => channel.SourceOrigin == SourceOrigin.LocalCatalog),
            state.Channels.Count(channel => channel.SourceOrigin == SourceOrigin.Manual),
            state.Channels.Count(channel => channel.SourceOrigin == SourceOrigin.Imported),
            state.Channels.Count(channel => channel.Pinned),
            state.HiddenCatalogUrls.Count,
            state.Collections.Count,
            state.ListeningHistory.Count,
            state.LastCatalogRefreshAt,
            generatedUtc,
            extras);

    /// <summary>
    /// Renders the summary as greppable <c>KEY=value</c> lines, matching the log's own shape so one
    /// reader habit covers both files.
    /// </summary>
    public static string Render(DiagnosticEnvironment environment)
    {
        var text = new StringBuilder();
        Append(text, "generated_utc", Timestamp(environment.GeneratedUtc));
        Append(text, "app_version", environment.AppVersion);
        Append(text, "os", environment.OperatingSystem);
        Append(text, "os_arch", environment.Architecture);
        Append(text, "ui_language", environment.InterfaceLanguage?.ToString() ?? LanguageNotChosen);
        Append(text, "media_backend", environment.MediaBackend.ToString());
        // Criterion 13: the author must be able to tell "this backend reports no statistics" from
        // "this session had no trouble" - the two look identical in a log otherwise.
        Append(text, "backend_stats", environment.MediaBackend == MediaBackend.LibVlc ? "detailed" : "session_only");
        Append(text, "state_schema", environment.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(text, "channels_total", Count(environment.TotalChannels));
        Append(text, "channels_catalog", Count(environment.CatalogChannels));
        Append(text, "channels_local_catalog", Count(environment.LocalCatalogChannels));
        Append(text, "channels_manual", Count(environment.ManualChannels));
        Append(text, "channels_imported", Count(environment.ImportedChannels));
        Append(text, "channels_pinned", Count(environment.PinnedChannels));
        Append(text, "channels_hidden", Count(environment.HiddenChannels));
        Append(text, "collections", Count(environment.Collections));
        Append(text, "history_entries", Count(environment.HistoryEntries));
        Append(text, "catalog_refreshed_utc", environment.CatalogRefreshedUtc is { } refreshed ? Timestamp(refreshed) : "never");
        // Facts only the host can learn (media engine build, output choice, proxy): the caller supplies them as
        // key/value pairs so this assembly stays free of any media or UI dependency. Values are flattened to one
        // line - a break inside one would start a fake entry - and a blank value is stated, never left empty.
        foreach (var (key, value) in environment.Extras ?? [])
        {
            var flat = value.ReplaceLineEndings(" ").Trim();
            Append(text, key, flat.Length == 0 ? "unknown" : flat);
        }

        return text.ToString();
    }

    private static void Append(StringBuilder text, string key, string value) =>
        text.Append(key).Append('=').Append(value).Append("\r\n");

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
