using System.Text.Json.Nodes;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0272: the playback command group - what a spelling means, which options go together, which streams a stop acts
// on, how a failed open is told, and what a status response may carry. The App executes; these decide.
public sealed class AppPlaybackCommandsTests
{
    private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Address = "https://example.test/live";

    private static AppCommandParse Parse(params string[] args) => AppCommandLine.Parse(args, AppCommandManifest.Embedded);

    private static AppPlaybackSession Audio(Guid? id = null, string title = "Radio", string state = AppPlaybackState.Playing) =>
        new(AppPlaybackSession.AudioKind, id ?? Guid.NewGuid(), title, state, Recording: false);

    private static AppPlaybackSession Video(Guid? id = null) =>
        new(AppPlaybackSession.VideoKind, id ?? Guid.NewGuid(), "Camera", AppPlaybackState.Playing, Recording: false);

    // ---- play: the three targets and what the verb refuses -------------------------------------------------------

    [Theory]
    [InlineData("play", "--url", Address)]
    [InlineData("play", "--id", Guid1)]
    [InlineData("play", "--id", Guid1, "--url", Address)]
    [InlineData("play", "--url", Address, "--id", Guid1)]
    [InlineData("play", "--file", "radio.spres")]
    [InlineData("play", "--url", Address, "--background")]
    [InlineData("play", "--background=false", "--url", Address)]
    [InlineData("play", "--url", "rtsp://camera.test/stream", "--json", "--result", "out.json")]
    public void PlayTakesExactlyOneTarget(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
        Assert.Equal("play", parse.Entry!.Name);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("play", "--background")]
    [InlineData("play", "--id", Guid1, "--file", "radio.spres")]
    [InlineData("play", "--url", Address, "--file", "radio.spres")]
    [InlineData("play", "--url", Address, "--url", "https://example.test/other")]
    [InlineData("play", "--id", Guid1, "--id", Guid1)]
    [InlineData("play", "--file", "a.spres", "--file", "b.spres")]
    [InlineData("play", "--url", "ftp://example.test/live")]
    [InlineData("play", "--url", "file:///C:/music/a.mp3")]
    [InlineData("play", "--url", @"\\server\share\a.mp3")]
    [InlineData("play", "--url", "not an address")]
    [InlineData("play", "--file", "radio.mp3")]
    [InlineData("play", "--id", "not-a-guid")]
    [InlineData("play", "--url")]
    public void PlayRefusesAMissingDoubledOrUnlaunchableTargetWithExit10(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandReason.BadArgument, parse.Reason);
        var refusal = parse.ToRefusal("id");
        Assert.Equal(AppCommandExit.Usage, refusal.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.DoesNotContain(Address, refusal.Message, StringComparison.Ordinal);
    }

    // ---- the frozen launch spellings ----------------------------------------------------------------------------

    [Theory]
    [InlineData("--url", "ftp://example.test/live")]
    [InlineData("--url", Address, "--url", Address)]
    [InlineData("--id", "not-a-guid")]
    [InlineData("--somethingelse")]
    [InlineData("--url", Address, "extra")]
    public void ALaunchSpellingTheShippedParserCallsInvalidIsRefusedWithExit10(params string[] args)
    {
        Assert.Equal(StreamLaunchTargetKind.Invalid, StreamLaunchRequest.Parse(args).Kind);

        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandExit.Usage, parse.ToRefusal("id").ExitCode);
    }

    [Theory]
    [InlineData("--url", Address)]
    [InlineData("--id", Guid1)]
    [InlineData("--id", Guid1, "--url", Address)]
    [InlineData("--url", Address, "--id", Guid1)]
    [InlineData("radio.spres")]
    public void ALaunchSpellingBecomesTheCanonicalArgumentsOfPlayAndBackAgain(params string[] args)
    {
        var parse = Parse(args);

        // What a request carries re-parses, with the verb in front, to the same target.
        var again = Parse(["play", .. parse.ArgumentsAfterVerb]);
        Assert.Equal(AppCommandParseKind.Verb, again.Kind);
        Assert.Equal(StreamLaunchRequest.Parse(args), AppPlaybackCommands.ToLaunchRequest(again.Options, workingDirectory: null));

        // The launch line an older copy, or this process as the first instance, reads gives the same target again.
        var launchLine = AppPlaybackCommands.LaunchArguments(parse.Options, workingDirectory: null);
        Assert.Equal(StreamLaunchRequest.Parse(args), StreamLaunchRequest.Parse(launchLine));
    }

    [Fact]
    public void ARelativeFileIsResolvedAgainstTheCallersWorkingDirectory()
    {
        var directory = Path.GetTempPath();
        var parse = Parse("play", "--file", Path.Combine("sub", "radio.spres"));

        var launch = AppPlaybackCommands.ToLaunchRequest(parse.Options, directory);

        Assert.Equal(StreamLaunchTargetKind.File, launch.Kind);
        Assert.Equal(Path.GetFullPath(Path.Combine("sub", "radio.spres"), directory), launch.File);
    }

    [Fact]
    public void AnAbsoluteFileIsLeftAsItIs()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "radio.spres");

        var launch = AppPlaybackCommands.ToLaunchRequest(Parse("play", "--file", absolute).Options, Path.GetTempPath());

        Assert.Equal(absolute, launch.File);
    }

    // ---- playback stop -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("playback", "stop")]
    [InlineData("playback", "stop", "--all")]
    [InlineData("playback", "stop", "--id", Guid1)]
    [InlineData("playback", "stop", "--all=false", "--id", Guid1)]
    public void StopTakesNothingOneIdOrAll(params string[] args)
    {
        Assert.Equal(AppCommandParseKind.Verb, Parse(args).Kind);
    }

    [Fact]
    public void StopRefusesAnIdTogetherWithAll()
    {
        var parse = Parse("playback", "stop", "--id", Guid1, "--all");

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandReason.BadArgument, parse.Reason);
    }

    [Fact]
    public void StopOfOneSessionActsOnItWithoutAnyOption()
    {
        var radio = Audio();

        var plan = AppPlaybackCommands.PlanStop("id", channelId: null, all: false, [radio]);

        Assert.Null(plan.Refusal);
        Assert.Equal([radio], plan.Targets);
    }

    [Fact]
    public void StopOfNothingIsAGoalStateNotAnError()
    {
        var plan = AppPlaybackCommands.PlanStop("id", channelId: null, all: false, []);

        Assert.Null(plan.Refusal);
        Assert.Empty(plan.Targets);
        Assert.Equal(AppPlaybackMessages.NothingPlaying, AppPlaybackMessages.Stopped(0));
    }

    [Fact]
    public void StopOfSeveralSessionsWithoutAChoiceIsRefusedNamingTheMissingOption()
    {
        var plan = AppPlaybackCommands.PlanStop("id", channelId: null, all: false, [Audio(), Video()]);

        Assert.Empty(plan.Targets);
        Assert.NotNull(plan.Refusal);
        Assert.Equal(AppCommandExit.Usage, plan.Refusal.ExitCode);
        Assert.Equal(AppCommandReason.BadArgument, plan.Refusal.Reason);
        Assert.Contains("--id", plan.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("--all", plan.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StopWithAnIdActsOnThatChannelOnlyAndAnUnknownIdIsNothingToStop()
    {
        var radio = Audio();
        var camera = Video();

        var one = AppPlaybackCommands.PlanStop("id", camera.ChannelId, all: false, [radio, camera]);
        var none = AppPlaybackCommands.PlanStop("id", Guid.NewGuid(), all: false, [radio, camera]);

        Assert.Equal([camera], one.Targets);
        Assert.Empty(none.Targets);
        Assert.Null(none.Refusal);
    }

    [Fact]
    public void StopWithAllActsOnEverySession()
    {
        var sessions = new[] { Audio(), Video(), Video() };

        var plan = AppPlaybackCommands.PlanStop("id", channelId: null, all: true, sessions);

        Assert.Equal(sessions, plan.Targets);
    }

    // ---- volume set ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("volume", "set", "--level", "0")]
    [InlineData("volume", "set", "--level", "100")]
    [InlineData("volume", "set", "--level", "30", "--target", "audio")]
    [InlineData("volume", "set", "--level", "30", "--target", "video", "--muted")]
    [InlineData("volume", "set", "--level", "30", "--target", "video", "--muted", "false")]
    [InlineData("volume", "set", "--level=30", "--target=video", "--muted=true")]
    public void VolumeSetTakesALevelAnOptionalTargetAndMuteForVideo(params string[] args)
    {
        Assert.Equal(AppCommandParseKind.Verb, Parse(args).Kind);
    }

    [Theory]
    [InlineData("volume", "set")]
    [InlineData("volume", "set", "--level", "101")]
    [InlineData("volume", "set", "--level", "-1")]
    [InlineData("volume", "set", "--level", "loud")]
    [InlineData("volume", "set", "--level", "30", "--muted")]
    [InlineData("volume", "set", "--level", "30", "--target", "audio", "--muted", "true")]
    [InlineData("volume", "set", "--level", "30", "--target", "Video")]
    [InlineData("volume", "set", "--level", "30", "--target", "speakers")]
    public void VolumeSetRefusesALevelOutsideRangeOrMuteOnTheAudioTargetWithExit10(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandExit.Usage, parse.ToRefusal("id").ExitCode);
    }

    [Fact]
    public void ABoolOptionTakesTheWordsTrueAndFalseAsItsValueAndNothingElse()
    {
        var off = Parse("volume", "set", "--level", "30", "--target", "video", "--muted", "false");
        var on = Parse("volume", "set", "--level", "30", "--target", "video", "--muted");

        Assert.Equal("false", off.Value("muted"));
        Assert.Equal("true", on.Value("muted"));
        Assert.Equal(AppCommandParseKind.Refused, Parse("status", "--json", "maybe").Kind);
        Assert.Equal(AppCommandParseKind.Verb, Parse("status", "--json", "true").Kind);
    }

    // ---- the answers ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PlaybackReachability.NetworkUnreachable, AppCommandExit.Network)]
    [InlineData(PlaybackReachability.NameNotResolved, AppCommandExit.Network)]
    [InlineData(PlaybackReachability.ChannelUnreachable, AppCommandExit.Network)]
    [InlineData(PlaybackReachability.NotProbed, AppCommandExit.Failed)]
    [InlineData(PlaybackReachability.HostReachable, AppCommandExit.Failed)]
    [InlineData(PlaybackReachability.Inconclusive, AppCommandExit.Failed)]
    public void AStreamThatDidNotOpenIsStreamFailedAndFifteenWhenTheHostCannotBeReached(PlaybackReachability reachability, int exit)
    {
        var response = AppPlaybackCommands.OpenFailed("id", reachability);

        Assert.Equal(AppCommandStatus.Failed, response.Status);
        Assert.Equal(AppCommandReason.StreamFailed, response.Reason);
        Assert.Equal(exit, response.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
    }

    [Fact]
    public void ThePlayingAnswerCarriesTheChannelItsTitleItsKindAndItsState()
    {
        var radio = Audio(title: "Radio One");

        var response = AppPlaybackCommands.Playing("id", radio);

        Assert.Equal(AppCommandStatus.Done, response.Status);
        Assert.Equal(AppCommandExit.Done, response.ExitCode);
        Assert.Equal(radio.ChannelId.ToString("D"), (string?)response.Data!["channelId"]);
        Assert.Equal("Radio One", (string?)response.Data["title"]);
        Assert.Equal("audio", (string?)response.Data["kind"]);
        Assert.Equal("playing", (string?)response.Data["state"]);
    }

    [Fact]
    public void TheResumeAnswerDropsTheTitleTheCallerDidNotName()
    {
        var radio = Audio(title: "Radio One");
        var playing = AppPlaybackCommands.Playing("id", radio);

        var response = AppPlaybackCommands.Resumed(playing);

        Assert.Equal(AppCommandStatus.Done, response.Status);
        Assert.Equal(AppCommandExit.Done, response.ExitCode);
        Assert.Equal(playing.Message, response.Message);
        Assert.False(response.Data!.ContainsKey("title"));
        Assert.Equal(radio.ChannelId.ToString("D"), (string?)response.Data["channelId"]);
        Assert.Equal("playing", (string?)response.Data["state"]);
        Assert.Equal("Radio One", (string?)playing.Data!["title"]);
    }

    [Fact]
    public void TheResumeAnswerPassesAFailureUnchanged()
    {
        var failed = AppPlaybackCommands.OpenFailed("id", PlaybackReachability.NameNotResolved);

        Assert.Same(failed, AppPlaybackCommands.Resumed(failed));
    }

    [Fact]
    public void TheNeedsPersonAndStreamFailedTokensAreShortLowerCaseWords()
    {
        Assert.Equal("needs-person", AppCommandReason.NeedsPerson);
        Assert.Equal("stream-failed", AppCommandReason.StreamFailed);
        Assert.All(new[] { AppCommandReason.NeedsPerson, AppCommandReason.StreamFailed }, reason => Assert.True(AppCommandLine.IsVerbWord(reason)));
    }

    // ---- playback status: what a response may carry --------------------------------------------------------------

    [Fact]
    public void ASessionIsKindChannelTitleStateAndRecordingAndNothingElse()
    {
        var json = Audio(title: "Radio One", state: AppPlaybackState.Paused).ToJson();

        Assert.Equal(["kind", "channelId", "title", "state", "recording"], json.Select(pair => pair.Key));
        Assert.Equal("paused", (string?)json["state"]);
        Assert.False((bool)json["recording"]!);
    }

    [Theory]
    [InlineData("rtsp://user:secret@camera.test/live", "secret")]
    [InlineData("Listen https://name:hunter2@radio.test/stream now", "hunter2")]
    [InlineData("https://radio.test/stream?token=abcdef123456&x=1", "abcdef123456")]
    public void ATitleThatIsAPastedAddressNeverCarriesItsCredentials(string title, string secret)
    {
        var safe = AppPlaybackCommands.SafeTitle(title);

        Assert.DoesNotContain(secret, safe, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Audio(title: title).ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleLosesControlCharactersAndIsBounded()
    {
        Assert.Equal("Radio One", AppPlaybackCommands.SafeTitle("  Radio One  "));
        Assert.Equal("AB", AppPlaybackCommands.SafeTitle("A\u0007B"));
        Assert.True(AppPlaybackCommands.SafeTitle(new string('x', 5000)).Length <= 202);
        Assert.Equal(string.Empty, AppPlaybackCommands.SafeTitle(null));
    }

    [Fact]
    public void ABroadcastCapabilityAddressInATitleIsRedacted()
    {
        const string capability = "fmsx://endpoint.test/b/secretbroadcastid";

        var safe = AppPlaybackCommands.SafeTitle("Live " + capability);

        Assert.DoesNotContain("secretbroadcastid", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSessionsDataIsAnObjectWithASessionsArrayEmptyWhenNothingPlays()
    {
        var empty = AppPlaybackCommands.SessionsData([]);
        var two = AppPlaybackCommands.SessionsData([Audio(), Video()]);

        Assert.Empty(empty["sessions"]!.AsArray());
        Assert.Equal(2, two["sessions"]!.AsArray().Count);
        Assert.IsType<JsonObject>(two["sessions"]!.AsArray()[0]);
    }

    [Fact]
    public void NoPlaybackSentenceCarriesAnAddressOrAPath()
    {
        var sentences = typeof(AppPlaybackMessages).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

        Assert.All(sentences, sentence =>
        {
            Assert.DoesNotContain("://", sentence.Replace("http://, https:// or rtsp://", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain(@":\", sentence, StringComparison.Ordinal);
        });
    }
}
