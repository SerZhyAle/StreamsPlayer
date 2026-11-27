using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// STRWIN-0305: the broadcast commands - which options go together, which source a verb names, which stop a request means,
// and every answer the producer's verdict earns. The App asks the producer; these decide.
public sealed class AppBroadcastCommandsTests
{
    private const string Id = "id";

    private static AppCommandParse Parse(params string[] args) => AppCommandLine.Parse(args, AppCommandManifest.Embedded);

    // ---- the parser ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("mic", BroadcastSource.Microphone)]
    [InlineData("system-audio", BroadcastSource.SystemSound)]
    [InlineData("screen", BroadcastSource.Screen)]
    [InlineData("camera", BroadcastSource.Camera)]
    public void BroadcastStartNamesOneSourceByItsWireName(string name, BroadcastSource expected)
    {
        var parse = Parse("broadcast", "start", "--source", name);

        Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
        Assert.Equal(AppBroadcastCommands.StartVerb, parse.Entry!.Name);
        Assert.Equal(expected, AppBroadcastCommands.SourceOf(parse.Options));
    }

    [Theory]
    [InlineData("broadcast", "start")]
    [InlineData("broadcast", "start", "--source", "mic", "--source", "screen")]
    [InlineData("broadcast", "start", "--source", "Mic")]
    [InlineData("broadcast", "start", "--source", "window")]
    [InlineData("broadcast", "stop", "--source", "mic", "--all")]
    [InlineData("broadcast", "stop", "--source", "mic", "--source", "camera")]
    [InlineData("broadcast", "start", "--source", "mic", "--network", "lan")]
    public void ABroadcastVerbWithoutItsOptionsIsUsageExit10(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandReason.BadArgument, parse.Reason);
        var refusal = parse.ToRefusal(Id);
        Assert.Equal(AppCommandStatus.Refused, refusal.Status);
        Assert.Equal(AppCommandReason.BadArgument, refusal.Reason);
        Assert.Equal(AppCommandExit.Usage, refusal.ExitCode);
    }

    [Fact]
    public void BroadcastStopTakesASourceOrAllOrNothing()
    {
        Assert.All(new[] { Parse("broadcast", "stop"), Parse("broadcast", "stop", "--all"), Parse("broadcast", "stop", "--source", "camera") },
            parse => Assert.Equal(AppCommandParseKind.Verb, parse.Kind));
        Assert.Null(AppBroadcastCommands.SourceOf(Parse("broadcast", "stop").Options));
    }

    // ---- the start ----------------------------------------------------------------------------------------------------

    [Fact]
    public void BroadcastingNotAllowedIsModuleOffExit11NamingTheSetting()
    {
        var refused = AppBroadcastCommands.StartRefusal(Id, BroadcastStartRefusal.NotAllowed)!;
        var guest = AppBroadcastCommands.StartRefusal(Id, BroadcastStartRefusal.GuestAccount)!;

        Assert.All(new[] { refused, guest }, response =>
        {
            Assert.Equal(AppCommandStatus.Refused, response.Status);
            Assert.Equal(AppCommandReason.ModuleOff, response.Reason);
            Assert.Equal(AppCommandExit.NotReady, response.ExitCode);
        });
        Assert.Contains("Allow this PC to broadcast", refused.Message, StringComparison.Ordinal);
        Assert.Null(AppBroadcastCommands.StartRefusal(Id, null));
        Assert.Null(AppBroadcastCommands.StartRefusal(Id, BroadcastStartRefusal.AlreadyRunning));
    }

    [Theory]
    [InlineData("BroadcastNotAllowed", AppCommandReason.ModuleOff, AppCommandExit.NotReady)]
    [InlineData("BroadcastGuestRefused", AppCommandReason.ModuleOff, AppCommandExit.NotReady)]
    [InlineData("BroadcastTooManySources", AppCommandReason.NoSpace, AppCommandExit.Resources)]
    [InlineData("BroadcastNoReachableEndpoint", AppCommandReason.Network, AppCommandExit.Network)]
    [InlineData("BroadcastRelayLost", AppCommandReason.Network, AppCommandExit.Network)]
    [InlineData("ExchangeUnavailable", AppCommandReason.Network, AppCommandExit.Network)]
    [InlineData("ExchangeNotPermitted", AppCommandReason.Network, AppCommandExit.Network)]
    [InlineData("BroadcastPermissionDenied", AppCommandReason.IoError, AppCommandExit.Io)]
    [InlineData("BroadcastDeviceInUse", AppCommandReason.IoError, AppCommandExit.Io)]
    [InlineData("BroadcastDeviceMissing", AppCommandReason.IoError, AppCommandExit.Io)]
    [InlineData("BroadcastCaptureFailed", AppCommandReason.IoError, AppCommandExit.Io)]
    [InlineData("BroadcastPortTaken", AppCommandReason.Error, AppCommandExit.Failed)]
    [InlineData("BroadcastEncoderMissing", AppCommandReason.Error, AppCommandExit.Failed)]
    [InlineData(null, AppCommandReason.Error, AppCommandExit.Failed)]
    [InlineData("SomethingNew", AppCommandReason.Error, AppCommandExit.Failed)]
    public void AStartThatDidNotGoLiveIsReadFromTheProducersVerdict(string? key, string reason, int exit)
    {
        var response = AppBroadcastCommands.StartFailed(Id, key);

        Assert.Equal(reason, response.Reason);
        Assert.Equal(exit, response.ExitCode);
        Assert.NotEqual(AppCommandStatus.Done, response.Status);
    }

    [Fact]
    public void ACallerThatStoppedWaitingIsCancelledExit16WhateverTheVerdict()
    {
        var response = AppBroadcastCommands.StartFailed(Id, "ExchangeUnavailable", cancelled: true);

        Assert.Equal(AppCommandReason.Cancelled, response.Reason);
        Assert.Equal(AppCommandExit.Stopped, response.ExitCode);
    }

    [Fact]
    public void APictureWithoutAnEncoderIsTargetMissingAndAStartUnderWayIsBusy()
    {
        var missing = AppBroadcastCommands.StartFailed(Id, "BroadcastVideoUnavailable");
        var busy = AppBroadcastCommands.StartUnderWay(Id);

        Assert.Equal(AppCommandReason.TargetMissing, missing.Reason);
        Assert.Equal(AppCommandExit.NotReady, missing.ExitCode);
        Assert.Equal(AppCommandStatus.Failed, missing.Status);
        Assert.Equal(AppCommandStatus.Busy, busy.Status);
        Assert.Equal(AppCommandExit.NotReady, busy.ExitCode);
    }

    [Fact]
    public void ALiveBroadcastSaysWhereItCanBeReceivedAndNothingMore()
    {
        var response = AppBroadcastCommands.Started(Id, BroadcastSource.Screen, already: true, lan: true, exchange: false);

        Assert.Equal(AppCommandStatus.Done, response.Status);
        Assert.Equal("screen", response.Data!["source"]!.GetValue<string>());
        Assert.True(response.Data!["already"]!.GetValue<bool>());
        Assert.True(response.Data["lan"]!.GetValue<bool>());
        Assert.False(response.Data["exchange"]!.GetValue<bool>());
        Assert.Equal(["already", "exchange", "lan", "source"], response.Data.Select(pair => pair.Key).Order());
    }

    // ---- the stop -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStopPlanActsOnWhatTheRequestNamesAndNeverGuesses()
    {
        BroadcastSource[] two = [BroadcastSource.Microphone, BroadcastSource.Screen];

        Assert.Equal(two, AppBroadcastCommands.PlanStop(Id, null, all: true, two).Targets);
        Assert.True(AppBroadcastCommands.PlanStop(Id, null, all: true, two).All);
        Assert.Equal([BroadcastSource.Screen], AppBroadcastCommands.PlanStop(Id, BroadcastSource.Screen, all: false, two).Targets);
        Assert.Empty(AppBroadcastCommands.PlanStop(Id, BroadcastSource.Camera, all: false, two).Targets);
        Assert.Equal([BroadcastSource.Microphone],
            AppBroadcastCommands.PlanStop(Id, null, all: false, [BroadcastSource.Microphone]).Targets);
        Assert.Empty(AppBroadcastCommands.PlanStop(Id, null, all: false, []).Targets);

        var several = AppBroadcastCommands.PlanStop(Id, null, all: false, two);
        Assert.Empty(several.Targets);
        Assert.Equal(AppCommandReason.BadArgument, several.Refusal!.Reason);
        Assert.Equal(AppCommandExit.Usage, several.Refusal!.ExitCode);
    }

    [Fact]
    public void NothingRunningIsNothingBroadcast()
    {
        var answer = AppCommandCaller.Oneshot(Id, AppCommandManifest.Embedded.Find(AppBroadcastCommands.StopVerb)!);
        var start = AppCommandCaller.Oneshot(Id, AppCommandManifest.Embedded.Find(AppBroadcastCommands.StartVerb)!);

        Assert.Equal(AppCommandStatus.Done, answer.Status);
        Assert.Equal(0, answer.Data!["stopped"]!.GetValue<int>());
        Assert.Equal(AppCommandExit.NotReady, start.ExitCode);
    }

    // ---- the manifest and the code agree ------------------------------------------------------------------------------

    [Fact]
    public void TheManifestShipsTheBroadcastGroupLast()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.Equal([AppBroadcastCommands.StartVerb, AppBroadcastCommands.StopVerb], manifest.Commands.TakeLast(2).Select(command => command.Name));
        Assert.Equal("1.2", manifest.CliVersion);
        var start = manifest.Find(AppBroadcastCommands.StartVerb)!;
        var stop = manifest.Find(AppBroadcastCommands.StopVerb)!;
        Assert.Equal("leaves-machine", start.Risk);
        Assert.Equal("safe", stop.Risk);
        Assert.Equal(AppCommandColdStart.Refuse, AppCommandPolicy.ColdStartOf(start));
        Assert.Equal(AppCommandColdStart.Oneshot, AppCommandPolicy.ColdStartOf(stop));
        Assert.Equal(30_000, start.TimeoutMs);
        Assert.Equal(10_000, stop.TimeoutMs);
        Assert.All(new[] { start, stop }, entry => Assert.True(entry.Idempotent));
        var source = start.Args.Single(argument => argument.Name == "source");
        Assert.True(source.Required);
        Assert.Equal(Enum.GetValues<BroadcastSource>().Select(BroadcastSources.WireName), source.Values);
        // B9 is the second step: until STRWIN-0303 has passed R1 to R10, neither verb is remote.
        Assert.All(new[] { start, stop }, entry => Assert.Null(entry.Remote));
    }

    [Fact]
    public void TheManifestNamesEveryKeyOfDataAndEveryExitTheCodeAnswers()
    {
        using var document = JsonDocument.Parse(AppCommandManifest.EmbeddedBytes);
        var commands = document.RootElement.GetProperty("commands").EnumerateArray().ToDictionary(
            command => command.GetProperty("name").GetString()!);

        static string[] Returns(JsonElement command) =>
            command.GetProperty("returns").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray();

        static int[] Exits(JsonElement command) =>
            command.GetProperty("exitCodes").EnumerateArray().Select(item => item.GetProperty("code").GetInt32()).ToArray();

        var live = AppBroadcastCommands.Started(Id, BroadcastSource.Microphone, already: false, lan: true, exchange: true);
        Assert.Equal(live.Data!.Select(pair => pair.Key).Order(), Returns(commands[AppBroadcastCommands.StartVerb]).Order());
        Assert.Equal(AppBroadcastCommands.Stopped(Id, 1).Data!.Select(pair => pair.Key), Returns(commands[AppBroadcastCommands.StopVerb]));

        string?[] keys = ["BroadcastNotAllowed", "BroadcastTooManySources", "BroadcastNoReachableEndpoint", "BroadcastPermissionDenied",
            "BroadcastPortTaken", "BroadcastVideoUnavailable"];
        var answered = keys.Select(key => AppBroadcastCommands.StartFailed(Id, key).ExitCode)
            .Append(AppBroadcastCommands.StartFailed(Id, null, cancelled: true).ExitCode)
            .Append(AppCommandExit.Done).Append(AppCommandExit.Usage).Append(AppCommandExit.Permission)
            .Distinct().Order();
        Assert.Equal(answered, Exits(commands[AppBroadcastCommands.StartVerb]));
        Assert.Equal([0, 10, 11], Exits(commands[AppBroadcastCommands.StopVerb]));
    }

    [Fact]
    public void EveryBroadcastSentenceIsOneLineWithNoAddress()
    {
        var sentences = typeof(AppBroadcastMessages).GetFields()
            .Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!)
            .Append(AppBroadcastMessages.Started("mic", already: false)).Append(AppBroadcastMessages.Stopped(3));

        Assert.All(sentences, sentence =>
        {
            Assert.DoesNotContain("://", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", sentence, StringComparison.Ordinal);
            Assert.EndsWith(".", sentence, StringComparison.Ordinal);
        });
    }
}
