using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0260 / <c>DEVICE-EXCHANGE</c> 1.12 items EN to ER: the product's own cases of the amendment's Conformance table -
/// 1 to 3 (the shapes, against the vendored samples), 10 and 11 (exclusions and groups), and the reader's whole-answer
/// rule of item ER. Cases 4 to 9 and 12 to 14 are the server's or need a live session.
/// </summary>
public sealed class StandardSettingsTests
{
    private static string Vector(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "settings-standard-v1", name));

    private static readonly CatalogState Customised = new()
    {
        Theme = AppTheme.Dark,
        TileSize = StreamTileSize.Large,
        ViewMode = CatalogViewMode.Grid,
        AnimatedBackdrop = false,
        HideAdultContent = true,
        MainWindowTopmost = true,
        PlayerWindowTopmost = true,
        KeepPlayerControlsVisible = true,
        VideoVolume = 40,
        VideoMuted = true,
        AudioVolume = 70,
        AudioChannelMode = AudioChannelMode.ReverseStereo,
        KeepAwakeDuringPlayback = false,
        SystemMediaControls = true,
        VideoBackend = MediaBackend.Flyleaf,
        UpdateStreamPreviews = false,
        Language = AppLanguage.Ukrainian,
        AudioOutputDevice = "{0.0.0.00000000}.{device}",
        FrameFolder = @"D:\Frames",
        VideoRecordingFolder = @"D:\Video",
        AudioRecordingFolder = @"D:\Audio",
        ResumePlaybackOnStartup = true,
        ResumeChannelIds = [Guid.NewGuid()]
    };

    [Fact]
    public void A_round_trip_restores_every_exchangeable_setting()
    {
        var payload = StandardSettings.Serialize(Customised, StandardSettings.Groups);
        var read = StandardSettings.Parse(StandardSettings.FormatVersion, payload);

        Assert.Equal(StandardSettingsReadFailure.None, read.Failure);
        var applied = StandardSettings.Apply(new CatalogState(), read.Document!, StandardSettings.Groups).State;
        foreach (var declaration in StandardSettings.Declarations)
        {
            Assert.Equal(declaration.Read(Customised), declaration.Read(applied));
        }

        Assert.Equal(StandardSettings.Declarations.Count, StandardSettings.Apply(new CatalogState(), read.Document!,
            StandardSettings.Groups).AppliedKeys.Count);
    }

    [Fact]
    public void The_payload_is_one_object_with_a_version_equal_to_the_format_and_groups_of_objects_within_the_bound()
    {
        var payload = StandardSettings.Serialize(Customised, StandardSettings.Groups);
        using var document = JsonDocument.Parse(payload);

        Assert.True(Encoding.UTF8.GetByteCount(payload) <= StandardSettings.MaximumPayloadBytes);
        Assert.Equal(StandardSettings.FormatVersion, document.RootElement.GetProperty("version").GetInt32());
        var groups = document.RootElement.GetProperty("groups").EnumerateObject().ToArray();
        Assert.Equal(StandardSettings.Groups, groups.Select(group => group.Name));
        Assert.All(groups, group => Assert.Equal(JsonValueKind.Object, group.Value.ValueKind));
        Assert.All(StandardSettings.Groups, group => Assert.Matches("^[a-z][a-z0-9-]{0,31}$", group));
    }

    [Fact]
    public void No_declared_exclusion_and_no_common_spelling_of_one_is_ever_written()
    {
        using var manifest = JsonDocument.Parse(Vector("07-local-exclusion-manifest.json"));
        var common = manifest.RootElement.GetProperty("mandatoryLocalExclusions").EnumerateArray().Select(item => item.GetString()!);
        var payload = StandardSettings.Serialize(Customised, StandardSettings.Groups);
        using var document = JsonDocument.Parse(payload);
        var written = document.RootElement.GetProperty("groups").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateObject().Select(member => member.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in StandardSettings.LocalOnlyKeys.Concat(common))
        {
            Assert.False(written.Contains(key), key);
            Assert.DoesNotContain(StandardSettings.Declarations, declaration =>
                string.Equals(declaration.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        // The values themselves: no folder, no device, no resume marker leaks under another name.
        Assert.DoesNotContain(@"D:\\", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("device}", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Customised.ResumeChannelIds[0].ToString(), payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_inbound_payload_carrying_local_only_keys_changes_none_of_them()
    {
        var hostile = """
            {"version":1,"groups":{
              "appearance":{"theme":"Light","windowBounds":{"left":0},"window_bounds":"0,0,10,10"},
              "playback":{"audioOutputDevice":"evil","frameFolder":"C:\\Windows","port_bindings":8080,"resumePlaybackOnStartup":true},
              "interface":{"language":"Russian","device_key":"x","passwords":"y"},
              "general":{"frameFolder":"C:\\Windows"}
            }}
            """;
        var read = StandardSettings.Parse(1, hostile);
        Assert.Equal(StandardSettingsReadFailure.None, read.Failure);

        var applied = StandardSettings.Apply(Customised, read.Document!, StandardSettings.Groups).State;

        Assert.Equal(AppTheme.Light, applied.Theme);
        Assert.Equal(AppLanguage.Russian, applied.Language);
        Assert.Equal(Customised.AudioOutputDevice, applied.AudioOutputDevice);
        Assert.Equal(Customised.FrameFolder, applied.FrameFolder);
        Assert.Equal(Customised.VideoRecordingFolder, applied.VideoRecordingFolder);
        Assert.Equal(Customised.AudioRecordingFolder, applied.AudioRecordingFolder);
        Assert.Equal(Customised.ResumePlaybackOnStartup, applied.ResumePlaybackOnStartup);
        Assert.Equal(Customised.ResumeChannelIds, applied.ResumeChannelIds);
    }

    [Fact]
    public void A_group_left_out_of_a_save_is_absent_and_a_group_left_out_of_a_read_is_not_applied()
    {
        var payload = StandardSettings.Serialize(Customised, [StandardSettings.Playback]);
        using (var document = JsonDocument.Parse(payload))
        {
            Assert.Equal([StandardSettings.Playback],
                document.RootElement.GetProperty("groups").EnumerateObject().Select(group => group.Name));
        }

        var full = StandardSettings.Parse(1, StandardSettings.Serialize(Customised, StandardSettings.Groups)).Document!;
        var applied = StandardSettings.Apply(new CatalogState(), full, [StandardSettings.Appearance]).State;

        Assert.Equal(AppTheme.Dark, applied.Theme);
        Assert.Equal(100, applied.VideoVolume);
        Assert.Equal(MediaBackend.LibVlc, applied.VideoBackend);
        Assert.Null(applied.Language);
    }

    [Fact]
    public void A_save_with_no_known_group_is_refused_before_anything_is_written()
    {
        Assert.Throws<ArgumentException>(() => StandardSettings.Serialize(Customised, []));
        Assert.Throws<ArgumentException>(() => StandardSettings.Serialize(Customised, ["general"]));
    }

    [Fact]
    public void A_language_never_chosen_is_not_written_and_an_absent_key_leaves_the_local_value()
    {
        var payload = StandardSettings.Serialize(new CatalogState(), [StandardSettings.Interface]);
        Assert.Equal("""{"version":1,"groups":{"interface":{}}}""", payload);

        var read = StandardSettings.Parse(1, payload).Document!;
        Assert.Equal(AppLanguage.German,
            StandardSettings.Apply(new CatalogState { Language = AppLanguage.German }, read, StandardSettings.Groups).State.Language);
    }

    [Fact]
    public void A_value_of_the_wrong_kind_or_range_is_ignored_and_the_rest_is_applied()
    {
        var payload = """
            {"version":1,"groups":{"appearance":{"theme":"Neon","tileSize":"7","animatedBackdrop":"yes","hideAdultContent":true},
             "playback":{"videoVolume":101,"audioVolume":-1,"videoMuted":true,"audioChannelMode":"Left, Right","videoBackend":"Flyleaf"}}}
            """;
        var applied = StandardSettings.Apply(new CatalogState(), StandardSettings.Parse(1, payload).Document!, StandardSettings.Groups);

        Assert.Equal(AppTheme.System, applied.State.Theme);
        Assert.Equal(StreamTileSize.Medium, applied.State.TileSize);
        Assert.True(applied.State.AnimatedBackdrop);
        Assert.True(applied.State.HideAdultContent);
        Assert.Equal(100, applied.State.VideoVolume);
        Assert.Equal(100, applied.State.AudioVolume);
        Assert.True(applied.State.VideoMuted);
        Assert.Equal(AudioChannelMode.Stereo, applied.State.AudioChannelMode);
        Assert.Equal(MediaBackend.Flyleaf, applied.State.VideoBackend);
        Assert.Equal(["appearance.hideAdultContent", "playback.videoMuted", "playback.videoBackend"], applied.AppliedKeys);
    }

    [Theory]
    [InlineData(2, """{"version":2,"groups":{}}""", StandardSettingsReadFailure.FormatUnsupported)]
    [InlineData(99, "not json at all", StandardSettingsReadFailure.FormatUnsupported)]
    [InlineData(0, """{"version":0,"groups":{}}""", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, """{"version":2,"groups":{}}""", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, """{"groups":{}}""", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, """{"version":1}""", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, """{"version":1,"groups":[]}""", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, """{"version":1,"groups":{"appearance":"dark"}}""", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, """[{"version":1,"groups":{}}]""", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, "{\"version\":1,", StandardSettingsReadFailure.Damaged)]
    [InlineData(1, null, StandardSettingsReadFailure.Damaged)]
    [InlineData(1, """{"version":1,"groups":{}}""", StandardSettingsReadFailure.None)]
    public void A_read_is_understood_as_a_whole_before_anything_is_applied(int formatVersion, string? payload,
        StandardSettingsReadFailure expected)
    {
        var read = StandardSettings.Parse(formatVersion, payload);

        Assert.Equal(expected, read.Failure);
        Assert.Equal(expected == StandardSettingsReadFailure.None, read.Document is not null);
    }

    [Fact]
    public void A_payload_above_the_bound_is_not_read()
    {
        var padding = new string('a', StandardSettings.MaximumPayloadBytes);
        var payload = "{\"version\":1,\"groups\":{\"appearance\":{\"x\":\"" + padding + "\"}}}";

        Assert.Equal(StandardSettingsReadFailure.Damaged, StandardSettings.Parse(1, payload).Failure);
    }

    [Fact]
    public void The_vendored_payload_of_another_program_is_read_and_its_unknown_groups_change_nothing()
    {
        using var answer = JsonDocument.Parse(Vector("04-get-standard-payload-response.json"));
        var root = answer.RootElement;
        var read = StandardSettings.Parse(root.GetProperty("formatVersion").GetInt32(), root.GetProperty("settingsPayload").GetString());

        Assert.Equal(StandardSettingsReadFailure.None, read.Failure);
        var applied = StandardSettings.Apply(Customised, read.Document!, StandardSettings.Groups);
        Assert.Empty(applied.AppliedKeys);
        Assert.Equal(Customised, applied.State);
        // The sample is fms_win's: this product never takes it as its own answer.
        Assert.Null(ExchangeSettingsFrames.ReadPayload(root));
    }

    [Fact]
    public void The_outbound_frames_have_the_members_and_order_of_the_vendored_samples()
    {
        static string[] Members(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject().Select(member => member.Name).ToArray();
        }

        var save = JsonSerializer.Serialize(ExchangeSettingsFrames.Save("{\"version\":1,\"groups\":{}}",
            new DateTimeOffset(2026, 11, 10, 4, 0, 0, TimeSpan.FromHours(2))), ExchangeProtocol.JsonOptions);
        var get = JsonSerializer.Serialize(ExchangeSettingsFrames.Get(), ExchangeProtocol.JsonOptions);

        Assert.Equal(Members(Vector("01-save-standard-request.json")), Members(save));
        Assert.Equal(Members(Vector("03-get-standard-request.json")), Members(get));
        using var saveDocument = JsonDocument.Parse(save);
        Assert.Equal("settings_save_standard", saveDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, saveDocument.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("streamsplayer", saveDocument.RootElement.GetProperty("appId").GetString());
        Assert.Equal(1, saveDocument.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("2026-11-10T02:00:00Z", saveDocument.RootElement.GetProperty("timestamp").GetString());
        using var getDocument = JsonDocument.Parse(get);
        Assert.Equal("settings_get_standard", getDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal("streamsplayer", getDocument.RootElement.GetProperty("appId").GetString());
    }

    [Fact]
    public void The_answers_are_read_only_when_they_are_this_programs()
    {
        using var saved = JsonDocument.Parse(Vector("02-save-standard-response.json").Replace("\"fms_win\"", "\"streamsplayer\""));
        var answer = ExchangeSettingsFrames.ReadSaved(saved.RootElement);
        Assert.NotNull(answer);
        Assert.Equal(1, answer.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero), answer.UpdatedAt);

        using var foreign = JsonDocument.Parse(Vector("02-save-standard-response.json"));
        Assert.Null(ExchangeSettingsFrames.ReadSaved(foreign.RootElement));

        using var payload = JsonDocument.Parse(Vector("04-get-standard-payload-response.json").Replace("\"fms_win\"", "\"streamsplayer\""));
        var read = ExchangeSettingsFrames.ReadPayload(payload.RootElement);
        Assert.NotNull(read);
        Assert.Equal(1, read.FormatVersion);
    }

    [Theory]
    [InlineData("05-refused-not-found.json", "not-found")]
    [InlineData("06-refused-incompatible.json", "version-incompatible")]
    public void A_refusal_is_shown_in_the_words_of_its_row_and_never_its_message(string vector, string code)
    {
        using var refused = JsonDocument.Parse(Vector(vector));
        var reason = refused.RootElement.GetProperty("reason").GetString();
        var row = ExchangeFailureRows.OfWire(reason);

        Assert.Equal(code, row.Code);
        Assert.Equal(ExchangeFailureKind.Wire, row.Kind);
        Assert.StartsWith("ExchangeSettings", row.MessageKey, StringComparison.Ordinal);
    }

    [Fact]
    public void The_four_frame_types_are_in_the_vocabulary_and_the_rejected_reason_is_not_a_row()
    {
        foreach (var type in new[] { "settings_save_standard", "settings_saved", "settings_get_standard", "settings_standard_payload" })
        {
            Assert.True(ExchangeProtocol.IsKnownType(type), type);
        }

        Assert.Equal("app-id-mismatch", ExchangeFailureRows.OfWire("app-id-mismatch").Code);
        Assert.Equal("invalid-payload", ExchangeFailureRows.OfWire("invalid-payload").Code);
        // `unauthorized` of the proposal was not adopted (item EP): it reads as the generic refusal.
        Assert.Equal(ExchangeFailureRows.Refused, ExchangeFailureRows.OfWire("unauthorized"));
    }
}
