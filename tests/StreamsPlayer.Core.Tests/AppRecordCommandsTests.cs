using System.Reflection;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0273: the recording command group - which options go together, where --out is, which stream a verb acts on, what a failed
// write is, and what every answer carries. The App executes; these decide.
public sealed class AppRecordCommandsTests
{
    private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Guid2 = "1f8fad5b-d9cb-469f-a165-70867728950e";

    private static readonly string Out = Path.Combine(Path.GetTempPath(), "sp0273-recordings");

    private static AppCommandParse Parse(params string[] args) => AppCommandLine.Parse(args, AppCommandManifest.Embedded);

    private static AppPlaybackSession Session(
        string kind = AppPlaybackSession.AudioKind, string? id = null, string state = AppPlaybackState.Playing, bool recording = false) =>
        new(kind, Guid.Parse(id ?? Guid.NewGuid().ToString()), "Radio", state, recording);

    // ---- the parser ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RecordStartTakesAFolderAndAnOptionalChannel()
    {
        var plain = Parse("record", "start", "--out", Out);
        var named = Parse("record", "start", "--out", Out, "--id", Guid1, "--result", "answer.json");
        var equals = Parse("record", "start", "--id=" + Guid1, "--out=" + Out);

        Assert.All(new[] { plain, named, equals }, parse =>
        {
            Assert.Equal(AppCommandParseKind.Verb, parse.Kind);
            Assert.Equal("record start", parse.Entry!.Name);
            Assert.Equal(Out, parse.Value("out"));
        });
        Assert.Equal(Guid1, named.Value("id"));
        Assert.Equal(Guid1, equals.Value("id"));
    }

    [Theory]
    [InlineData("record", "start")]
    [InlineData("record", "start", "--out")]
    [InlineData("record", "start", "--out", "")]
    [InlineData("record", "start", "--out", "   ")]
    [InlineData("record", "start", "--out", "bad|folder")]
    [InlineData("record", "start", "--out", "bad\"folder")]
    [InlineData("record", "start", "--out", "C:\\one", "--out", "C:\\two")]
    [InlineData("record", "start", "--out", "C:\\one", "--id", Guid1, "--id", Guid2)]
    [InlineData("record", "start", "--out", "C:\\one", "--id", "not-a-guid")]
    [InlineData("record", "start", "--out", "C:\\one", "--bogus", "x")]
    [InlineData("record", "start", "C:\\one")]
    public void RecordStartRefusesAMissingDoubledOrUnusableTargetWithExit10(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandReason.BadArgument, parse.Reason);
        var refusal = parse.ToRefusal("id");
        Assert.Equal(AppCommandExit.Usage, refusal.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.DoesNotContain("C:\\", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordStopTakesAnOptionalChannelAndNothingElse()
    {
        Assert.Equal(AppCommandParseKind.Verb, Parse("record", "stop").Kind);
        Assert.Equal(AppCommandParseKind.Verb, Parse("record", "stop", "--id", Guid1, "--result", "answer.json").Kind);
    }

    [Theory]
    [InlineData("record", "stop", "--id", Guid1, "--id", Guid2)]
    [InlineData("record", "stop", "--id", "not-a-guid")]
    [InlineData("record", "stop", "--out", "C:\\one")]
    [InlineData("record", "stop", "now")]
    public void RecordStopRefusesADoubledOrUnknownOptionWithExit10(params string[] args)
    {
        var parse = Parse(args);

        Assert.Equal(AppCommandParseKind.Refused, parse.Kind);
        Assert.Equal(AppCommandExit.Usage, parse.ToRefusal("id").ExitCode);
    }

    // ---- --out --------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnAbsoluteOutIsTakenInFullWithoutItsTrailingSeparator()
    {
        var (folder, violation) = AppRecordCommands.ResolveOut(Out + Path.DirectorySeparatorChar, workingDirectory: null);

        Assert.Null(violation);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Out)), folder);
    }

    [Fact]
    public void ARelativeOutIsResolvedAgainstTheCallersWorkingFolder()
    {
        var working = Path.GetTempPath();
        var relative = Path.Combine("sub", "rec");

        var (folder, violation) = AppRecordCommands.ResolveOut(relative, working);

        Assert.Null(violation);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(relative, working)), folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-folder")]
    public void ARelativeOutWithNoUsableWorkingFolderIsRefusedNeverResolvedAgainstTheAppsOwn(string? working)
    {
        var (folder, violation) = AppRecordCommands.ResolveOut("rec", working);

        Assert.Null(folder);
        Assert.Equal(AppCommandReason.BadArgument, violation!.Value.Reason);
        Assert.Equal(AppRecordMessages.OutNeedsFullPath, violation.Value.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad|folder")]
    public void AnOutThatIsNotAPathIsRefused(string? value)
    {
        var (folder, violation) = AppRecordCommands.ResolveOut(value, Path.GetTempPath());

        Assert.Null(folder);
        Assert.Equal(AppRecordMessages.OutNotUsable, violation!.Value.Message);
    }

    [Fact]
    public void TheSameFolderIsRecognisedWhateverTheCaseOrTheTrailingSeparator()
    {
        Assert.True(AppRecordCommands.SameFolder(Out, Out + Path.DirectorySeparatorChar));
        Assert.True(AppRecordCommands.SameFolder(Out, Out.ToUpperInvariant()));
        Assert.False(AppRecordCommands.SameFolder(Out, Out + "-other"));
        Assert.False(AppRecordCommands.SameFolder(Out, null));
        Assert.False(AppRecordCommands.SameFolder(" ", Out));
    }

    // ---- the stream a verb acts on ------------------------------------------------------------------------------------

    [Fact]
    public void StartWithNothingOpenIsTargetMissingExit11()
    {
        var plan = AppRecordCommands.PlanStart("id", null, []);

        Assert.Null(plan.Target);
        Assert.Equal(AppCommandExit.NotReady, plan.Refusal!.ExitCode);
        Assert.Equal(AppCommandReason.TargetMissing, plan.Refusal.Reason);
        Assert.Equal(AppRecordMessages.NothingOpen, plan.Refusal.Message);
    }

    [Fact]
    public void StartTakesTheOnlyOpenStreamAndRefusesToGuessBetweenSeveral()
    {
        var only = Session(id: Guid1);
        Assert.Same(only, AppRecordCommands.PlanStart("id", null, [only]).Target);

        var refusal = AppRecordCommands.PlanStart("id", null, [only, Session(AppPlaybackSession.VideoKind, Guid2)]).Refusal;

        Assert.Equal(AppCommandExit.Usage, refusal!.ExitCode);
        Assert.Equal(AppCommandReason.BadArgument, refusal.Reason);
        Assert.Contains("--id", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartTakesTheNamedStreamAndRefusesOneThatIsNotOpen()
    {
        var radio = Session(id: Guid1);
        var video = Session(AppPlaybackSession.VideoKind, Guid2);

        Assert.Same(video, AppRecordCommands.PlanStart("id", Guid.Parse(Guid2), [radio, video]).Target);
        var missing = AppRecordCommands.PlanStart("id", Guid.NewGuid(), [radio, video]).Refusal;

        Assert.Equal(AppCommandExit.NotReady, missing!.ExitCode);
        Assert.Equal(AppRecordMessages.StreamNotOpen, missing.Message);
    }

    [Theory]
    [InlineData(AppPlaybackState.Paused, AppCommandExit.NotReady)]
    [InlineData(AppPlaybackState.Failed, AppCommandExit.NotReady)]
    public void APausedRadioAndAFailedWindowAreNotStreamsThatCanBeRecorded(string state, int exit)
    {
        var plan = AppRecordCommands.PlanStart("id", null, [Session(state: state)]);

        Assert.Null(plan.Target);
        Assert.Equal(exit, plan.Refusal!.ExitCode);
        Assert.Equal(AppCommandReason.TargetMissing, plan.Refusal.Reason);
    }

    [Theory]
    [InlineData(AppPlaybackState.Playing)]
    [InlineData(AppPlaybackState.Buffering)]
    public void APlayingOrBufferingStreamIsATargetAndSoIsOneAlreadyRecorded(string state)
    {
        Assert.NotNull(AppRecordCommands.PlanStart("id", null, [Session(state: state)]).Target);
        Assert.NotNull(AppRecordCommands.PlanStart("id", null, [Session(state: state, recording: true)]).Target);
    }

    [Fact]
    public void StopTakesTheOnlyRecordingWhateverElseIsOpen()
    {
        var recording = Session(id: Guid1, recording: true);
        var plan = AppRecordCommands.PlanStop("id", null, [Session(AppPlaybackSession.VideoKind, Guid2), recording]);

        Assert.Same(recording, plan.Target);
        Assert.Null(plan.Refusal);
    }

    [Fact]
    public void StopWithNothingRecordedIsTargetMissingExit11()
    {
        var refusal = AppRecordCommands.PlanStop("id", null, [Session(), Session(AppPlaybackSession.VideoKind)]).Refusal;

        Assert.Equal(AppCommandExit.NotReady, refusal!.ExitCode);
        Assert.Equal(AppCommandReason.TargetMissing, refusal.Reason);
        Assert.Equal(AppRecordMessages.NothingRecording, refusal.Message);
    }

    [Fact]
    public void StopWithSeveralRecordingsAndNoChoiceIsRefusedExit10()
    {
        var refusal = AppRecordCommands.PlanStop("id", null,
            [Session(recording: true), Session(AppPlaybackSession.VideoKind, recording: true)]).Refusal;

        Assert.Equal(AppCommandExit.Usage, refusal!.ExitCode);
        Assert.Equal(AppCommandReason.BadArgument, refusal.Reason);
        Assert.Contains("--id", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StopOfANamedStreamNeedsThatStreamToBeRecording()
    {
        var recording = Session(id: Guid1, recording: true);
        var idle = Session(AppPlaybackSession.VideoKind, Guid2);

        Assert.Same(recording, AppRecordCommands.PlanStop("id", Guid.Parse(Guid1), [recording, idle]).Target);
        Assert.Equal(AppRecordMessages.NotRecording, AppRecordCommands.PlanStop("id", Guid.Parse(Guid2), [recording, idle]).Refusal!.Message);
        Assert.Equal(AppRecordMessages.StreamNotOpen, AppRecordCommands.PlanStop("id", Guid.NewGuid(), [recording, idle]).Refusal!.Message);
    }

    // ---- a full disk or an unwritable folder --------------------------------------------------------------------------

    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    public void AnIoFailureWithADiskFullErrorIsNoSpace(int hresult)
    {
        Assert.Equal(AppRecordWriteProblem.NoSpace, AppRecordCommands.ClassifyWrite(new IOException("full", hresult)));
    }

    [Fact]
    public void EveryOtherRefusalOfAFolderIsNotWritable()
    {
        Assert.Equal(AppRecordWriteProblem.NotWritable, AppRecordCommands.ClassifyWrite(new IOException("busy")));
        Assert.Equal(AppRecordWriteProblem.NotWritable, AppRecordCommands.ClassifyWrite(new UnauthorizedAccessException()));
        Assert.Equal(AppRecordWriteProblem.NotWritable, AppRecordCommands.ClassifyWrite(new DirectoryNotFoundException()));
        // Only an I/O failure can be a full disk, whatever number a stray exception carries.
        Assert.Equal(AppRecordWriteProblem.NotWritable,
            AppRecordCommands.ClassifyWrite(new InvalidOperationException("x") { HResult = unchecked((int)0x80070070) }));
    }

    [Fact]
    public void AWriteFailureIsExit13WithNoSpaceAndExit14WithIoError()
    {
        var full = AppRecordCommands.WriteFailed("id", AppRecordWriteProblem.NoSpace);
        var refused = AppRecordCommands.WriteFailed("id", AppRecordWriteProblem.NotWritable);

        Assert.Equal((AppCommandStatus.Failed, AppCommandExit.Resources, AppCommandReason.NoSpace), (full.Status, full.ExitCode, full.Reason));
        Assert.Equal((AppCommandStatus.Failed, AppCommandExit.Io, AppCommandReason.IoError), (refused.Status, refused.ExitCode, refused.Reason));
        Assert.Null(full.Data);
    }

    [Fact]
    public void AFailedWriteStillReportsWhatWasSavedBeforeIt()
    {
        var saved = new[] { new AppRecordedFile(Path.Combine(Out, "a.mp3"), 2048, TimeSpan.FromSeconds(7)) };

        var response = AppRecordCommands.WriteFailed("id", AppRecordWriteProblem.NoSpace, saved);

        Assert.Equal(AppCommandExit.Resources, response.ExitCode);
        Assert.Equal(2048L, (long)response.Data!["bytes"]!);
        Assert.Equal(7, (int)response.Data["seconds"]!);
    }

    [Theory]
    [InlineData(true, AppCommandExit.Resources, AppCommandReason.NoSpace)]
    [InlineData(false, AppCommandExit.Io, AppCommandReason.IoError)]
    public void AStrandedSegmentIsToldAsTheCauseThatStrandedIt(bool noSpace, int exit, string reason)
    {
        var response = AppRecordCommands.Stranded("id", noSpace);

        Assert.Equal((exit, reason), (response.ExitCode, response.Reason));
        Assert.Equal(AppRecordMessages.Stranded, response.Message);
    }

    // ---- the answers --------------------------------------------------------------------------------------------------

    [Fact]
    public void ARadioStartAnswersTheFolderTheKindAndTheFile()
    {
        var file = Path.Combine(Out, "radio.mp3");

        var response = AppRecordCommands.Started("id", AppPlaybackSession.AudioKind, Out, file, already: false);

        Assert.Equal((AppCommandStatus.Done, AppCommandExit.Done), (response.Status, response.ExitCode));
        Assert.Equal(["folder", "kind", "file"], response.Data!.Select(pair => pair.Key));
        Assert.Equal(file, (string)response.Data["file"]!);
        Assert.Equal(AppRecordMessages.Started("audio"), response.Message);
    }

    [Fact]
    public void AVideoStartAnswersOnlyTheFolderAndTheKindBecauseItsFileIsNamedWhenItEnds()
    {
        var response = AppRecordCommands.Started("id", AppPlaybackSession.VideoKind, Out, file: null, already: true);

        Assert.Equal(["folder", "kind"], response.Data!.Select(pair => pair.Key));
        Assert.Equal(AppRecordMessages.AlreadyRecording, response.Message);
    }

    [Fact]
    public void AFinishedRecordingAnswersTheFirstFileTheTotalsAndTheNumberOfParts()
    {
        var files = new[]
        {
            new AppRecordedFile(Path.Combine(Out, "a.ts"), 1_000_000, TimeSpan.FromSeconds(1.25)),
            new AppRecordedFile(Path.Combine(Out, "b.ts"), 3_000_000_000, TimeSpan.FromSeconds(1.25))
        };

        var response = AppRecordCommands.Finished("id", files);

        Assert.Equal(["file", "bytes", "seconds", "parts"], response.Data!.Select(pair => pair.Key));
        Assert.Equal(files[0].Path, (string)response.Data["file"]!);
        Assert.Equal(3_001_000_000L, (long)response.Data["bytes"]!);
        Assert.Equal(3, (int)response.Data["seconds"]!); // 2.5 s rounds away from zero
        Assert.Equal(2, (int)response.Data["parts"]!);
        Assert.Equal(AppRecordMessages.Finished(2), response.Message);
        Assert.Equal(AppRecordMessages.Finished(1), AppRecordMessages.Finished(0));
    }

    [Fact]
    public void AFinishedRecordingWithNoFileIsNotAnAnswer()
    {
        Assert.Throws<ArgumentException>(() => AppRecordCommands.FinishedData([]));
    }

    [Fact]
    public void EveryOtherAnswerCarriesItsExitReasonAndStatus()
    {
        AssertAnswer(AppRecordCommands.NotRecordable("id", AppRecordMessages.NotRecordableBroadcast),
            AppCommandStatus.Failed, AppCommandExit.Failed, AppCommandReason.NotRecordable);
        AssertAnswer(AppRecordCommands.StartFailed("id"), AppCommandStatus.Failed, AppCommandExit.Failed, AppCommandReason.Error);
        AssertAnswer(AppRecordCommands.NothingSaved("id"), AppCommandStatus.Failed, AppCommandExit.Failed, AppCommandReason.Error);
        AssertAnswer(AppRecordCommands.StationNotAnswering("id"), AppCommandStatus.Failed, AppCommandExit.Failed, AppCommandReason.StreamFailed);
        AssertAnswer(AppRecordCommands.NotConfirmed("id", AppRecordMessages.StartNotConfirmed),
            AppCommandStatus.Failed, AppCommandExit.NotProven, AppCommandReason.Timeout);
        AssertAnswer(AppRecordCommands.OtherFolder("id"), AppCommandStatus.Busy, AppCommandExit.NotReady, AppCommandReason.Busy);
        AssertAnswer(AppRecordCommands.RecordingBusy("id"), AppCommandStatus.Busy, AppCommandExit.NotReady, AppCommandReason.Busy);
        AssertAnswer(AppRecordCommands.OutIsFile("id"), AppCommandStatus.Refused, AppCommandExit.Usage, AppCommandReason.BadArgument);
        AssertAnswer(AppRecordCommands.TargetMissing("id", AppRecordMessages.NothingOpen),
            AppCommandStatus.Failed, AppCommandExit.NotReady, AppCommandReason.TargetMissing);
    }

    private static void AssertAnswer(AppCommandResponse response, string status, int exit, string reason)
    {
        Assert.Equal((status, exit, reason), (response.Status, response.ExitCode, response.Reason));
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
    }

    [Fact]
    public void NoSentenceCarriesAnAddressAPathOrMoreThanOneLine()
    {
        var sentences = typeof(AppRecordMessages)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Append(AppRecordMessages.Started("video"))
            .Append(AppRecordMessages.Finished(3))
            .ToArray();

        Assert.True(sentences.Length > 20);
        Assert.All(sentences, sentence =>
        {
            Assert.EndsWith(".", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("://", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain(":\\", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("\\\\", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", sentence, StringComparison.Ordinal);
        });
    }

    // ---- the manifest and the code agree ------------------------------------------------------------------------------

    [Fact]
    public void TheManifestShipsTheRecordGroupAfterThePlaybackGroup()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.Equal(["record start", "record stop"], manifest.Commands.Skip(11).Select(command => command.Name));
        Assert.All(new[] { "record start", "record stop" }, verb =>
        {
            var entry = manifest.Find(verb)!;
            Assert.Equal("writes-user-data", entry.Risk);
            Assert.Equal("refuse", entry.ColdStart);
            Assert.Equal("done", entry.Wait);
            Assert.True(entry.Idempotent);
            Assert.Empty(entry.Aliases);
        });
        Assert.Equal(15_000, manifest.Find("record start")!.TimeoutMs);
        Assert.Equal(30_000, manifest.Find("record stop")!.TimeoutMs);
        var outArgument = manifest.Find("record start")!.Args.Single(argument => argument.Name == "out");
        Assert.True(outArgument.Required);
        Assert.Equal("path", outArgument.Kind);
        Assert.Equal(["id", "result"], manifest.Find("record stop")!.Args.Select(argument => argument.Name));
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

        var radio = AppRecordCommands.Started("id", AppPlaybackSession.AudioKind, Out, Path.Combine(Out, "a.mp3"), already: false);
        Assert.Equal(radio.Data!.Select(pair => pair.Key).Order(), Returns(commands["record start"]).Order());
        var finished = AppRecordCommands.FinishedData([new AppRecordedFile(Path.Combine(Out, "a.mp3"), 1, TimeSpan.FromSeconds(1))]);
        Assert.Equal(finished.Select(pair => pair.Key).Order(), Returns(commands["record stop"]).Order());

        int[] answered = [0, 1, 2, 10, 11, 12, 13, 14];
        Assert.Equal(answered, Exits(commands["record start"]));
        Assert.Equal(answered, Exits(commands["record stop"]));
    }

    // SP-0279, CAPTURE-OUTPUT 0.8: a folder the caller names is never created, so a missing one is the thing the verb names
    // not existing - failed with target-missing and exit 11 (rule 12) - and is told apart from a file at the path (exit 10).
    [Fact]
    public void AMissingOutFolderIsFailedTargetMissingExit11AndNeverCreated()
    {
        var missing = AppRecordCommands.OutMissing("r");

        Assert.Equal(AppCommandStatus.Failed, missing.Status);
        Assert.Equal(AppCommandReason.TargetMissing, missing.Reason);
        Assert.Equal(AppCommandExit.NotReady, missing.ExitCode);
        Assert.Equal(AppRecordMessages.OutMissing, missing.Message);
        Assert.DoesNotContain(Out, missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(AppRecordCommands.OutIsFile("r").ExitCode, missing.ExitCode);
    }

    [Fact]
    public void TheTokensAreTheOnesTheProposalAndTheManifestUse()
    {
        Assert.Equal("no-space", AppCommandReason.NoSpace);
        Assert.Equal("io-error", AppCommandReason.IoError);
        Assert.Equal("not-recordable", AppCommandReason.NotRecordable);
    }
}
