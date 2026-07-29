using System.Text.RegularExpressions;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0040 phase 02: the environment file that travels with the logs. Criterion 3 is a negative
/// fact - what must NOT be in it - so most of these tests assert absence.
/// </summary>
public sealed class DiagnosticEnvironmentSummaryTests
{
    private const string SecretTitle = "Zhyhunenko Private Camera";
    private const string SecretUrl = "https://camera.example.invalid/private/front-door.m3u8";

    private static readonly DateTimeOffset Generated = new(2026, 7, 30, 1, 2, 3, TimeSpan.Zero);

    private static CatalogState MixedState() => new()
    {
        Language = AppLanguage.Ukrainian,
        VideoBackend = MediaBackend.Flyleaf,
        LastCatalogRefreshAt = new DateTimeOffset(2026, 7, 28, 15, 46, 0, TimeSpan.Zero),
        HiddenCatalogUrls = ["https://hidden.example.invalid/one"],
        Collections = [new ChannelCollection { Id = Guid.NewGuid(), Name = "Morning radio" }],
        Channels =
        [
            Channel(SecretTitle, SecretUrl, SourceOrigin.Manual, pinned: true),
            Channel("Catalog one", "https://catalog.example.invalid/one", SourceOrigin.Catalog),
            Channel("Catalog two", "https://catalog.example.invalid/two", SourceOrigin.Catalog, pinned: true),
            Channel("Imported", "https://imported.example.invalid/list", SourceOrigin.Imported)
        ]
    };

    [Fact]
    public void From_CountsEveryOriginPinAndHide()
    {
        var environment = DiagnosticEnvironmentSummary.From(MixedState(), "26.0730.0012", "Windows", "X64", Generated);

        Assert.Equal(4, environment.TotalChannels);
        Assert.Equal(2, environment.CatalogChannels);
        Assert.Equal(1, environment.ManualChannels);
        Assert.Equal(1, environment.ImportedChannels);
        Assert.Equal(2, environment.PinnedChannels);
        Assert.Equal(1, environment.HiddenChannels);
        Assert.Equal(1, environment.Collections);
        Assert.Equal(AppLanguage.Ukrainian, environment.InterfaceLanguage);
        Assert.Equal(MediaBackend.Flyleaf, environment.MediaBackend);
    }

    // The whole point of criterion 3: this file is mailed to a person, so it must carry the shape of
    // the installation and none of its content.
    [Fact]
    public void Render_CarriesNoUrlAndNoChannelTitle()
    {
        var text = DiagnosticEnvironmentSummary.Render(
            DiagnosticEnvironmentSummary.From(MixedState(), "26.0730.0012", "Windows", "X64", Generated));

        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtsp", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretTitle, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Morning radio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MediaBackend.LibVlc, "detailed")]
    [InlineData(MediaBackend.Flyleaf, "session_only")]
    public void Render_NamesTheBackendAndHowMuchDetailItReports(MediaBackend backend, string expected)
    {
        var state = MixedState() with { VideoBackend = backend };

        var text = DiagnosticEnvironmentSummary.Render(
            DiagnosticEnvironmentSummary.From(state, "26.0730.0012", "Windows", "X64", Generated));

        Assert.Contains($"media_backend={backend}\r\n", text);
        Assert.Contains($"backend_stats={expected}\r\n", text);
    }

    [Fact]
    public void Render_StatesAnUnchosenLanguageExplicitly()
    {
        var text = DiagnosticEnvironmentSummary.Render(
            DiagnosticEnvironmentSummary.From(new CatalogState(), "26.0730.0012", "Windows", "X64", Generated));

        Assert.Contains($"ui_language={DiagnosticEnvironmentSummary.LanguageNotChosen}\r\n", text);
        Assert.Contains("catalog_refreshed_utc=never\r\n", text);
        Assert.Contains("generated_utc=2026-07-30T01:02:03Z\r\n", text);
    }

    [Fact]
    public void Render_EmitsOnlyGreppableKeyValueLines()
    {
        var text = DiagnosticEnvironmentSummary.Render(
            DiagnosticEnvironmentSummary.From(MixedState(), "26.0730.0012", "Windows", "X64", Generated));

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Matches(new Regex(@"^[a-z0-9_]+=\S.*$"), line));
    }

    private static StreamChannel Channel(string title, string url, SourceOrigin origin, bool pinned = false) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = title,
        MediaKind = MediaKind.Video,
        SourceOrigin = origin,
        Pinned = pinned,
        AddedAt = Generated
    };
}
