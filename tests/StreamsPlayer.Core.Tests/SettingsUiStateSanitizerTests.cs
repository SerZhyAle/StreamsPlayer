using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0191 / SP-0180 wave C: a hand-edited or damaged <c>settings-ui.json</c> can deserialize into nulls the
/// record's declared shape forbids. The window indexes those dictionaries from a dispatcher callback and
/// from its constructor, so what cannot be used is dropped at the door.
/// </summary>
public sealed class SettingsUiStateSanitizerTests
{
    private static SettingsUiState Read(string json) =>
        JsonSerializer.Deserialize<SettingsUiState>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void NullDictionariesBecomeEmptyOnes()
    {
        var read = Read("""{"groups":null,"viewports":null,"lastPageIndex":3}""");

        var sanitized = SettingsUiStateSanitizer.Sanitize(read);

        Assert.NotNull(sanitized.Groups);
        Assert.NotNull(sanitized.Viewports);
        Assert.Empty(sanitized.Groups);
        Assert.Empty(sanitized.Viewports);
        Assert.Equal(3, sanitized.LastPageIndex);
    }

    [Fact]
    public void ANullOrAnonymousViewportEntryIsDroppedAndTheRestSurvive()
    {
        var read = Read("""
            {"groups":{"library.tv":false},
             "viewports":{"page0":null,"page1":{"groupId":null,"offsetWithinGroup":4},
                          "page2":{"groupId":"","offsetWithinGroup":4},
                          "page3":{"groupId":"audio.output","offsetWithinGroup":12.5}}}
            """);

        var sanitized = SettingsUiStateSanitizer.Sanitize(read);

        var kept = Assert.Single(sanitized.Viewports);
        Assert.Equal("page3", kept.Key);
        Assert.Equal("audio.output", kept.Value.GroupId);
        Assert.Equal(12.5, kept.Value.OffsetWithinGroup);
        Assert.False(sanitized.Groups["library.tv"]);
    }

    [Fact]
    public void AWellFormedStateIsCarriedOverUnchanged()
    {
        var state = new SettingsUiState
        {
            LastPageIndex = 2,
            Groups = new Dictionary<string, bool> { ["general.basics"] = false },
            Viewports = new Dictionary<string, SettingsViewportAnchor> { ["page2"] = new("playback.video", 8) },
            Window = new SettingsWindowRectangle(1, 2, 900, 700)
        };

        var sanitized = SettingsUiStateSanitizer.Sanitize(state);

        Assert.Equal(2, sanitized.LastPageIndex);
        Assert.Equal(state.Groups, sanitized.Groups);
        Assert.Equal(state.Viewports, sanitized.Viewports);
        Assert.Equal(state.Window, sanitized.Window);
    }
}
