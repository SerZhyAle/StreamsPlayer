using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// STRWIN-0303: the target side of REMOTE-CONTROL 0.4 that Core holds - cases R1 to R7 and R12 by unit test. The App's
// one-at-a-time worker and the 60-second prompt are the owner's two-device rung (D8); here the decision it runs on.
public sealed class RemoteControlTests
{
    private const string RequestId = "11111111-2222-4333-8444-555555555555";
    private const string Controller = "controller-device";
    private const string Stranger = "stranger-device";

    private static readonly AppCommandManifest Manifest = AppCommandManifest.Embedded;

    private static readonly string[] FirstSlice =
        ["commands", "version", "status", "show", "quit", "play", "playback stop", "playback pause", "playback resume", "volume set"];

    private static RemoteControlSettings Listening(RemoteControllerGrant? grant = null) =>
        new(true, [grant ?? RemoteControllerGrant.Ticked(Controller, "Phone")]);

    private static RemoteDecision Decide(RemoteControlSettings settings, string verb, string controller = Controller) =>
        RemoteControlPolicy.Decide(RequestId, settings, controller, verb, Manifest);

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static byte[] Payload(string json) => Encoding.UTF8.GetBytes(json);

    private static string Now(int offsetSeconds = 0) =>
        DateTimeOffset.UtcNow.AddSeconds(offsetSeconds).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    // ----- the manifest member of rule 7 (A1) -------------------------------------------------------------------

    [Fact]
    public void ExactlyTheFirstSliceIsRemoteAndEachConfirmsByGrant()
    {
        var remote = Manifest.Commands.Where(command => command.Remote is not null).Select(command => command.Name).ToArray();

        Assert.Equal(FirstSlice, remote);
        Assert.All(Manifest.Commands.Where(command => command.Remote is not null), command =>
        {
            Assert.Equal(AppCommandRemote.ConfirmGrant, command.Remote!.Confirm);
            Assert.Equal(RemoteRiskClass.Safe, command.Risk);
        });
        Assert.Equal("1.2", Manifest.CliVersion);
    }

    [Fact]
    public void TheRemoteViewListsOnlyRemoteEntriesWithoutPathOrJsonOptions()
    {
        var view = RemoteCommandArguments.RemoteView(AppCommandManifest.EmbeddedBytes.Span);
        var commands = view["commands"]!.AsArray().OfType<JsonObject>().ToArray();

        Assert.Equal(FirstSlice, commands.Select(command => command["name"]!.GetValue<string>()));
        var options = commands.SelectMany(command => command["args"]!.AsArray().OfType<JsonObject>()).ToArray();
        Assert.DoesNotContain(options, option => option["kind"]!.GetValue<string>() == RemoteCommandArguments.PathKind);
        Assert.DoesNotContain(options, option => option["name"]!.GetValue<string>() == RemoteCommandArguments.JsonOption);
        Assert.Contains(options, option => option["name"]!.GetValue<string>() == "url");
    }

    // ----- R1 to R3: the switch, the controller, the class --------------------------------------------------------

    [Fact]
    public void R1_SwitchOffIsNotPermittedEvenForATickedController()
    {
        var settings = Listening() with { Listening = false };

        var decision = Decide(settings, "show");

        Assert.Equal(RemoteDecisionKind.Refuse, decision.Kind);
        Assert.Equal(RemoteCommandReason.NotPermitted, decision.Refusal!.Reason);
        Assert.Equal(AppCommandExit.Permission, decision.Refusal.ExitCode);
    }

    [Fact]
    public void AbsentSettingsAreOffWithNoGrants()
    {
        Assert.False(RemoteControlSettings.Off.Listening);
        Assert.Empty(RemoteControlSettings.Off.Grants);
        Assert.Equal(RemoteCommandReason.NotPermitted, Decide(RemoteControlSettings.Off, "status").Refusal!.Reason);
    }

    [Fact]
    public void R2_AControllerThatIsNotTickedIsNotPermittedAndLearnsNoVerb()
    {
        var known = Decide(Listening(), "show", Stranger);
        var unknown = Decide(Listening(), "no-such-verb", Stranger);

        Assert.Equal(RemoteCommandReason.NotPermitted, known.Refusal!.Reason);
        Assert.Equal(RemoteCommandReason.NotPermitted, unknown.Refusal!.Reason);
    }

    [Fact]
    public void R3_AClassThatIsNotGrantedIsNotPermitted()
    {
        var grant = RemoteControllerGrant.Ticked(Controller, "Phone").With(RemoteRiskClass.Safe, null);

        var decision = Decide(Listening(grant), "playback pause");

        Assert.Equal(RemoteDecisionKind.Refuse, decision.Kind);
        Assert.Equal(RemoteCommandReason.NotPermitted, decision.Refusal!.Reason);
    }

    [Fact]
    public void ATickedControllerGetsSafeAlwaysAndNothingElse()
    {
        var grant = RemoteControllerGrant.Ticked(Controller, "Phone");

        Assert.Equal([RemoteRiskClass.Safe], grant.Classes.Keys);
        Assert.Equal(RemoteConsent.Always, grant.ConsentFor(RemoteRiskClass.Safe));
        Assert.Equal(RemoteConsent.Ask, RemoteConsent.DefaultFor(RemoteRiskClass.ReadsUserData));
        Assert.Equal(RemoteDecisionKind.Run, Decide(Listening(grant), "volume set").Kind);
    }

    // ----- R4: consent ------------------------------------------------------------------------------------------

    [Fact]
    public void R4_AskConsentAsksAndTheDeclinedAnswerIsRefusedWithExit16()
    {
        var grant = RemoteControllerGrant.Ticked(Controller, "Phone").With(RemoteRiskClass.Safe, RemoteConsent.Ask);

        var decision = Decide(Listening(grant), "quit");
        var declined = RemoteCommandAnswer.Declined(RequestId);

        Assert.Equal(RemoteDecisionKind.Ask, decision.Kind);
        Assert.Equal("quit", decision.Entry!.Name);
        Assert.Equal(RemoteCommandReason.Declined, declined.Reason);
        Assert.Equal(AppCommandExit.Stopped, declined.ExitCode);
    }

    // ----- R5: verbs and arguments ------------------------------------------------------------------------------

    [Theory]
    [InlineData("record start")]
    [InlineData("playback status")]
    [InlineData("channel add")]
    [InlineData("no-such-verb")]
    public void R5_AVerbOutsideTheRemoteSetOrTheManifestIsUnknown(string verb)
    {
        var decision = Decide(Listening(), verb);

        Assert.Equal(RemoteDecisionKind.Refuse, decision.Kind);
        Assert.Equal(AppCommandReason.UnknownVerb, decision.Refusal!.Reason);
        Assert.Equal(AppCommandExit.Usage, decision.Refusal.ExitCode);
    }

    [Theory]
    [InlineData("volume set", """{"loud":true}""")]
    [InlineData("play", """{"file":"C:\\Users\\a.mp3"}""")]
    [InlineData("play", """{"result":"C:\\out.json"}""")]
    [InlineData("play", """{"json":true}""")]
    [InlineData("volume set", """{"level":"high"}""")]
    public void R5_AnUnknownPathOutputOrMistypedMemberIsABadArgument(string verb, string args)
    {
        var entry = Manifest.Find(verb)!;

        var words = RemoteCommandArguments.ToWords(entry, Args(args), RequestId, out var refusal);

        Assert.Null(words);
        Assert.Equal(AppCommandReason.BadArgument, refusal!.Reason);
    }

    [Fact]
    public void R5_RemoteArgumentsBecomeTheWordsTheLocalParserReads()
    {
        var entry = Manifest.Find("volume set")!;

        var words = RemoteCommandArguments.ToWords(entry, Args("""{"level":40,"muted":false}"""), RequestId, out var refusal);
        var parse = AppCommandLine.Parse(["volume", "set", .. words!], Manifest);

        Assert.Null(refusal);
        Assert.Equal(["--level", "40", "--muted", "false"], words);
        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
    }

    // ----- R6: the payload --------------------------------------------------------------------------------------

    [Fact]
    public void R6_AVersionAboveOneIsASchemaRefusal()
    {
        var request = RemoteCommandRequest.TryParse(Payload($$"""{"v":2,"verb":"show","issuedAt":"{{Now()}}"}"""), RequestId, out var refusal);

        Assert.Null(request);
        Assert.Equal(AppCommandReason.Schema, refusal!.Reason);
    }

    [Theory]
    [InlineData("""{"v":1,"verb":"show","issuedAt":"2026-11-20T10:00:00Z","extra":1}""")]
    [InlineData("""{"v":1,"verb":"show"}""")]
    [InlineData("""{"v":1,"verb":"show","issuedAt":"2026-11-20T10:00:00Z","ttlSeconds":5}""")]
    [InlineData("""{"v":1,"verb":"show","issuedAt":"2026-11-20T10:00:00Z","args":[]}""")]
    [InlineData("""[1]""")]
    public void R6_AMalformedRequestIsABadArgument(string json)
    {
        var request = RemoteCommandRequest.TryParse(Payload(json), RequestId, out var refusal);

        Assert.Null(request);
        Assert.Equal(AppCommandReason.BadArgument, refusal!.Reason);
    }

    [Fact]
    public void R6_AWellFormedRequestIsReadWithTheDefaultTimeToLive()
    {
        var request = RemoteCommandRequest.TryParse(
            Payload($$"""{"v":1,"verb":"playback pause","issuedAt":"{{Now()}}"}"""), RequestId, out var refusal);

        Assert.Null(refusal);
        Assert.Equal("playback pause", request!.Verb);
        Assert.Equal(RemoteCommandRequest.DefaultTtlSeconds, request.TtlSeconds);
    }

    [Fact]
    public void AnAnswerAboveTheCapDropsDataAndSaysItWasTruncated()
    {
        var data = new JsonObject { ["small"] = "x", ["large"] = new string('a', RemoteCommandAnswer.MaximumPayloadBytes) };

        var bytes = RemoteCommandAnswer.Serialize(AppCommandResponse.Done(RequestId, "done", data));
        var answer = JsonNode.Parse(bytes)!.AsObject();

        Assert.True(bytes.Length <= RemoteCommandAnswer.MaximumPayloadBytes);
        Assert.True(answer["truncated"]!.GetValue<bool>());
        Assert.Equal("x", answer["data"]!["small"]!.GetValue<string>());
        Assert.Null(answer["data"]!["large"]);
    }

    // ----- R7: the time window ----------------------------------------------------------------------------------

    [Fact]
    public void R7_ARequestOutsideItsWindowIsExpired()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(RemoteCommandWindow.IsExpired(now.AddSeconds(-(120 + RemoteCommandWindow.ClockAllowance + 1)), 120, now));
        Assert.True(RemoteCommandWindow.IsExpired(now.AddSeconds(RemoteCommandWindow.ClockAllowance + 1), 120, now));
        Assert.False(RemoteCommandWindow.IsExpired(now.AddSeconds(-60), 120, now));
        Assert.False(RemoteCommandWindow.IsExpired(now.AddSeconds(RemoteCommandWindow.ClockAllowance - 1), 120, now));
        Assert.Equal(RemoteCommandReason.Expired, RemoteCommandAnswer.Expired(RequestId).Reason);
    }

    // ----- R12: the ring, also across a restart -----------------------------------------------------------------

    [Fact]
    public async Task R12_TheSameRequestIdIsAnsweredFromTheRingAlsoAfterItIsReloadedFromDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strwin0303-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RemoteControlStore(directory);
            var ring = new RemoteCommandRing();
            var keepUntil = RemoteCommandWindow.KeepUntil(DateTimeOffset.UtcNow, 120);
            ring.Record(Controller, RequestId, keepUntil, """{"v":1,"status":"done"}""");
            Assert.True(await store.SaveRingAsync(ring));

            var reloaded = await new RemoteControlStore(directory).LoadRingAsync();

            Assert.Equal("""{"v":1,"status":"done"}""", reloaded.AnswerOf(Controller, RequestId));
            Assert.Null(reloaded.AnswerOf(Stranger, RequestId));
            Assert.False(reloaded.Prune(DateTimeOffset.UtcNow));
            Assert.True(reloaded.Prune(keepUntil.AddSeconds(1)));
            Assert.Null(reloaded.AnswerOf(Controller, RequestId));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnUnreadableSettingsDocumentReadsAsOffAndIsNotSavedOver()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strwin0303-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, RemoteControlStore.SettingsFileName);
            await File.WriteAllTextAsync(path, "{ not json");
            var store = new RemoteControlStore(directory);

            var settings = await store.LoadSettingsAsync();
            var saved = await store.SaveSettingsAsync(Listening());

            Assert.False(settings.Listening);
            Assert.False(saved);
            Assert.Equal("{ not json", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsRoundTripAndAnUnknownClassOrConsentIsDropped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strwin0303-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RemoteControlStore(directory);
            var grant = new RemoteControllerGrant(Controller, "Phone", new Dictionary<string, string>
            {
                [RemoteRiskClass.Safe] = RemoteConsent.Ask,
                ["root"] = RemoteConsent.Always,
                [RemoteRiskClass.ReadsUserData] = "sometimes"
            });
            Assert.True(await store.SaveSettingsAsync(new RemoteControlSettings(true, [grant, grant])));

            var loaded = await new RemoteControlStore(directory).LoadSettingsAsync();

            Assert.True(loaded.Listening);
            var only = Assert.Single(loaded.Grants);
            Assert.Equal([RemoteRiskClass.Safe], only.Classes.Keys);
            Assert.Equal(RemoteConsent.Ask, only.ConsentFor(RemoteRiskClass.Safe));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ARevokedControllerDropsOutWithItsGrants()
    {
        var settings = new RemoteControlSettings(true,
            [RemoteControllerGrant.Ticked(Controller, "Phone"), RemoteControllerGrant.Ticked(Stranger, "Old tablet")]);

        var kept = settings.Retaining(new HashSet<string>(StringComparer.Ordinal) { Controller });

        Assert.Equal([Controller], kept.Grants.Select(grant => grant.DeviceId));
    }

    // ----- rule 11: the log -------------------------------------------------------------------------------------

    [Fact]
    public void TheLogKeepsTheLastEntriesAndRedactsEveryValue()
    {
        var log = new RemoteCommandLog();
        for (var index = 0; index < RemoteCommandLog.MaximumEntries + 3; index++)
        {
            log.Add(new RemoteCommandLogEntry(DateTimeOffset.UtcNow, "Phone", Controller, true, "show", RemoteRiskClass.Safe,
                AppCommandStatus.Done, index, RemoteCommandLog.RedactedArguments(["url"])));
        }

        Assert.Equal(RemoteCommandLog.MaximumEntries, log.Entries.Count);
        Assert.Equal(RemoteCommandLog.MaximumEntries + 2, log.Latest(1)[0].Exit);
        Assert.Equal(["--url=<redacted>"], log.Entries[0].Arguments);
    }
}
