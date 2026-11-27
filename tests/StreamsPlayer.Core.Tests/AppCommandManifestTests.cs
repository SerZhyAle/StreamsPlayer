using System.Text;
using System.Text.Json.Nodes;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0271: APP-COMMANDS rule 18 and case C1 - one manifest, the shipped bytes, the five base commands of rule 17.
public sealed class AppCommandManifestTests
{
    [Fact]
    public void TheEmbeddedManifestIsDocsCliCommandsJsonByteForByte()
    {
        var source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "docs", "cli-commands.json"));

        Assert.Equal(source, AppCommandManifest.EmbeddedBytes.ToArray());
    }

    [Fact]
    public void TheManifestShipsTheFiveBaseCommandsAsRule17DeclaresThem()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.Equal("StreamsPlayer", manifest.Product);
        Assert.Equal("StreamsPlayer.exe", manifest.Exe);
        Assert.Equal(["commands", "version", "status", "show", "quit"], manifest.Commands.Take(5).Select(command => command.Name));
        Assert.All(manifest.Commands.Take(5), command => Assert.Equal("safe", command.Risk));
        Assert.Equal("oneshot", manifest.Find("commands")!.ColdStart);
        Assert.Equal("oneshot", manifest.Find("version")!.ColdStart);
        Assert.Equal("refuse", manifest.Find("status")!.ColdStart);
        Assert.Equal("start", manifest.Find("show")!.ColdStart);
        Assert.Equal("oneshot", manifest.Find("quit")!.ColdStart);
        Assert.All(manifest.Commands, command => Assert.InRange(command.TimeoutMs, 1, AppCommandLimits.MaximumTimeoutMs));
    }

    // SP-0272: the playback group, declared as the research draft of SP-0270 states it.
    [Fact]
    public void TheManifestShipsThePlaybackGroupAfterTheBaseCommands()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.Equal(
            ["play", "playback stop", "playback pause", "playback resume", "playback status", "volume set"],
            manifest.Commands.Skip(5).Take(6).Select(command => command.Name));
        Assert.Equal("reads-user-data", manifest.Find("playback status")!.Risk);
        // SP-0273: the two record verbs write the user's files and are the only other verbs above safe.
        // STRWIN-0305: "broadcast start" sends the screen, a camera or the sound off the PC.
        Assert.All(manifest.Commands.Where(command => command.Name is not ("playback status" or "record start" or "record stop" or "broadcast start")),
            command => Assert.Equal("safe", command.Risk));
        Assert.All(new[] { "record start", "record stop" }, verb => Assert.Equal("writes-user-data", manifest.Find(verb)!.Risk));
        Assert.Equal("leaves-machine", manifest.Find("broadcast start")!.Risk);
        Assert.Equal("start", manifest.Find("play")!.ColdStart);
        Assert.Equal("oneshot", manifest.Find("playback stop")!.ColdStart);
        Assert.All(new[] { "playback pause", "playback resume", "playback status", "volume set" },
            verb => Assert.Equal("refuse", manifest.Find(verb)!.ColdStart));
        Assert.Equal(30_000, manifest.Find("play")!.TimeoutMs);
        Assert.Equal(30_000, manifest.Find("playback resume")!.TimeoutMs);
        Assert.Equal(10_000, manifest.Find("playback stop")!.TimeoutMs);
        Assert.All(new[] { "playback pause", "playback status", "volume set" }, verb => Assert.Equal(5_000, manifest.Find(verb)!.TimeoutMs));
        Assert.All(manifest.Commands.Skip(5), command => Assert.True(command.Idempotent, command.Name));
    }

    [Fact]
    public void TheBareStartIsTheEmptyAliasOfShowAndPlayOwnsTheLaunchSpellings()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.Same(manifest.Find("show"), manifest.FindByAlias(string.Empty));
        Assert.Equal(
            ["--url {url}", "--id {id}", "--id {id} --url {url}", "{file}"],
            manifest.Find("play")!.Aliases);
        Assert.All(manifest.Commands.Where(command => command.Name is not ("show" or "play")), command => Assert.Empty(command.Aliases));
    }

    // SP-0279, APP-COMMANDS items E and F: every shipped spelling is an object that says it is legacy; the bare start keeps the
    // start it always made and returns no answer; a typed value is a placeholder that maps to an option the verb has.
    [Fact]
    public void EveryShippedSpellingIsALegacyAliasObjectAndTheBareStartReturnsNoAnswer()
    {
        var manifest = AppCommandManifest.Embedded;
        var bare = Assert.Single(manifest.Find("show")!.AliasDetails);

        Assert.Equal(string.Empty, bare.Spelling);
        Assert.True(bare.Legacy);
        Assert.False(bare.Answers);

        var play = manifest.Find("play")!;
        Assert.Equal(4, play.AliasDetails.Count);
        Assert.All(play.AliasDetails, alias =>
        {
            Assert.True(alias.Legacy, alias.Spelling);
            Assert.True(alias.Answers, alias.Spelling);
            Assert.All(alias.Maps, pair => Assert.NotNull(play.FindArgument(pair.Value)));
            Assert.Equal(alias.Spelling.Count(character => character == '{'), alias.Maps.Count);
        });
        Assert.Equal("url", play.AliasDetails[0].Maps["url"]);
        Assert.Equal("file", play.AliasDetails[3].Maps["file"]);
    }

    // SP-0279, item C: commands and version print what the executable in hand knows, so no running copy does that work.
    [Fact]
    public void CommandsAndVersionAreForwardNeverAndEveryOtherVerbIsForwarded()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.All(new[] { "commands", "version" }, verb =>
        {
            var entry = manifest.Find(verb)!;
            Assert.Equal(AppCommandManifest.ForwardNever, entry.Forward);
            Assert.Equal("oneshot", entry.ColdStart);
            Assert.Equal("done", entry.Wait);
        });
        Assert.All(manifest.Commands.Where(command => command.Name is not ("commands" or "version")),
            command => Assert.Equal(AppCommandManifest.ForwardAlways, command.Forward));
    }

    [Fact]
    public void AForwardNeverVerbThatIsNotOneshotDoesNotLoad()
    {
        var manifest = EmbeddedNode();
        manifest["commands"]![0]!["coldStart"] = "refuse";

        Assert.Throws<FormatException>(() => AppCommandManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [Fact]
    public void AForwardOutsideAlwaysAndNeverDoesNotLoad()
    {
        var manifest = EmbeddedNode();
        manifest["commands"]![0]!["forward"] = "sometimes";

        Assert.Throws<FormatException>(() => AppCommandManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [Fact]
    public void AnAliasObjectWithoutASpellingDoesNotLoad()
    {
        var manifest = EmbeddedNode();
        manifest["commands"]![3]!["aliases"] = new JsonArray(new JsonObject { ["legacy"] = true });

        Assert.Throws<FormatException>(() => AppCommandManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [Fact]
    public void AnAliasThatIsPlainTextStillLoadsAsItsSpelling()
    {
        var manifest = EmbeddedNode();
        manifest["commands"]![3]!["aliases"] = new JsonArray("--legacy-show");

        var entry = AppCommandManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())).Find("show")!;

        Assert.Equal(["--legacy-show"], entry.Aliases);
        Assert.False(Assert.Single(entry.AliasDetails).Legacy);
    }

    // SP-0279, item H: an address that may carry a credential is sensitive and has its file option beside it.
    [Fact]
    public void TheAddressOptionsAreSensitiveAndEachHasItsFileOption()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.All(new[] { "play", "channel add" }, verb =>
        {
            var entry = manifest.Find(verb)!;
            Assert.True(entry.FindArgument("url")!.Sensitive, verb);
            Assert.NotNull(entry.FindArgument(AppUrlFile.OptionName));
        });
        Assert.All(manifest.Commands, command => Assert.All(command.Args.Where(argument => argument.Sensitive),
            argument => Assert.NotNull(command.FindArgument(argument.Name + "-file"))));
    }

    private static JsonNode EmbeddedNode() => JsonNode.Parse(AppCommandManifest.EmbeddedBytes.ToArray())!;

    [Fact]
    public void AManifestWithoutAColdStartDoesNotLoad()
    {
        var text = Encoding.UTF8.GetString(AppCommandManifest.EmbeddedBytes.Span)
            .Replace("\"coldStart\": \"refuse\",", string.Empty, StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => AppCommandManifest.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void AManifestWithAnUnknownRiskClassDoesNotLoad()
    {
        var text = Encoding.UTF8.GetString(AppCommandManifest.EmbeddedBytes.Span)
            .Replace("\"risk\": \"safe\"", "\"risk\": \"harmless\"", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => AppCommandManifest.Parse(Encoding.UTF8.GetBytes(text)));
    }
}
