using System.Text.Json.Nodes;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0275: the pieces of the second process that the player's entry point and the console companion share - one answer
// mapping, one wait for a close, one set of nothing-to-do answers, one output rule - so a command cannot be answered
// differently by the two executables.
public sealed class AppCommandCallerTests
{
    private const string Id = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public void AnAnsweredDeliveryIsTheAnswerItself()
    {
        var answer = AppCommandResponse.Done(Id, "ready");

        var response = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.Answered, answer), SingleInstanceLockResult.HeldByAnotherCopy, Id, Unexpected);

        Assert.Same(answer, response);
    }

    [Fact]
    public void ALegacyReplyIsLeftToTheCallerAndNeverBecomesDone()
    {
        var older = AppCommandCaller.OlderInstance(Id);

        var response = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.LegacyReply, LegacyReply: "ok"), SingleInstanceLockResult.HeldByAnotherCopy, Id, () => older);

        Assert.Same(older, response);
        Assert.Equal(AppCommandExit.NotProven, response.ExitCode);
        Assert.Equal(AppCommandReason.Schema, response.Reason);
    }

    // SP-0279, APP-COMMANDS item K: a hang-up with no line is positive evidence of an older copy - the same answer as a plain
    // reply, and the caller's one resend of a frozen alias happens inside the callback - and is not the deadline's timeout.
    [Fact]
    public void AClosedConnectionIsTheOlderCopyAndNeverTheTimeoutOrDone()
    {
        var older = AppCommandCaller.OlderInstance(Id);
        var resent = 0;

        var response = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.Closed), SingleInstanceLockResult.HeldByAnotherCopy, Id, () =>
            {
                resent++;
                return older;
            });

        Assert.Same(older, response);
        Assert.Equal(1, resent);
        Assert.Equal(AppCommandExit.NotProven, response.ExitCode);
        Assert.Equal(AppCommandMessages.OlderInstance, response.Message);
        Assert.NotEqual(AppCommandReason.Timeout, response.Reason);
    }

    [Fact]
    public void SilenceUntilTheDeadlineIsNeverTheOlderCopyAndSendsNothingAgain()
    {
        var response = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.NoAnswer), SingleInstanceLockResult.HeldByAnotherCopy, Id, Unexpected);

        Assert.Equal(AppCommandReason.Timeout, response.Reason);
        Assert.NotEqual(AppCommandMessages.OlderInstance, response.Message);
    }

    // SP-0279, item I.3: an answer whose request id is empty or foreign proves nothing about this command, so the second
    // process exits 2 and never with the answer's own code - even when that code is 0.
    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-0000000000ff")]
    public void AnAnswerWithAnEmptyOrForeignRequestIdIsExit2AndNotTheAnswersCode(string answerId)
    {
        var answer = AppCommandResponse.Done(answerId, "someone else's answer");

        var response = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.Unreadable, answer), SingleInstanceLockResult.HeldByAnotherCopy, Id, Unexpected);

        Assert.Equal(AppCommandExit.NotProven, response.ExitCode);
        Assert.Equal(AppCommandStatus.Failed, response.Status);
        Assert.Equal(Id, response.RequestId);
        Assert.Equal(AppCommandMessages.UnreadableAnswer, response.Message);
        Assert.NotEqual(answer.Message, response.Message);
    }

    [Fact]
    public void NoConnectionWithAHeldLockIsBusyAndWithAnInaccessibleLockIsElevation()
    {
        var held = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.NotConnected), SingleInstanceLockResult.HeldByAnotherCopy, Id, Unexpected);
        var elevated = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.NotConnected), SingleInstanceLockResult.Inaccessible, Id, Unexpected);

        Assert.Equal(AppCommandExit.NotReady, held.ExitCode);
        Assert.Equal(AppCommandStatus.Busy, held.Status);
        Assert.Equal(AppCommandExit.Permission, elevated.ExitCode);
        Assert.Equal(AppCommandReason.Elevation, elevated.Reason);
    }

    [Fact]
    public void NoAnswerInTimeIsTimeoutExit2()
    {
        var response = AppCommandCaller.Interpret(
            new AppCommandExchange(AppCommandDelivery.NoAnswer), SingleInstanceLockResult.HeldByAnotherCopy, Id, Unexpected);

        Assert.Equal(AppCommandExit.NotProven, response.ExitCode);
        Assert.Equal(AppCommandReason.Timeout, response.Reason);
    }

    [Fact]
    public void AClosedFirstInstanceIsDoneAndOneThatIsStillClosingIsExit2()
    {
        var identity = new SingleInstanceIdentity(
            @"Local\sp-test-caller-lock-" + Guid.NewGuid().ToString("N"), "sp-test-caller-pipe-" + Guid.NewGuid().ToString("N"));

        var closed = AppCommandCaller.AwaitClose(identity, Id, TimeSpan.FromMilliseconds(200));
        Assert.Equal(AppCommandExit.Done, closed.ExitCode);

        using var release = new ManualResetEventSlim();
        using var ready = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            SingleInstanceLock.TryAcquire(identity.MutexName, TimeSpan.Zero, out var held);
            ready.Set();
            release.Wait();
            held?.Dispose();
        })
        {
            IsBackground = true
        };
        holder.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var closing = AppCommandCaller.AwaitClose(identity, Id, TimeSpan.FromMilliseconds(200));

            Assert.Equal(AppCommandExit.NotProven, closing.ExitCode);
            Assert.Equal(AppCommandReason.Timeout, closing.Reason);
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void QuitAndPlaybackStopHaveNothingToDoWhenNothingRunsAndEveryOtherVerbIsNotRunning()
    {
        var manifest = AppCommandManifest.Embedded;

        var quit = AppCommandCaller.Oneshot(Id, manifest.Find("quit")!);
        var stop = AppCommandCaller.Oneshot(Id, manifest.Find(AppPlaybackCommands.StopVerb)!);
        var other = AppCommandCaller.Oneshot(Id, manifest.Find("status")!);

        Assert.Equal(AppCommandExit.Done, quit.ExitCode);
        Assert.Equal(AppCommandMessages.NothingToQuit, quit.Message);
        Assert.Equal(AppCommandExit.Done, stop.ExitCode);
        Assert.Equal(0, stop.Data!["stopped"]!.GetValue<int>());
        Assert.Equal(AppCommandExit.NotReady, other.ExitCode);
        Assert.Equal(AppCommandReason.NotRunning, other.Reason);
    }

    [Fact]
    public void TheVersionAnswerCarriesTheProductTheStampAndTheCommandSetVersion()
    {
        var manifest = AppCommandManifest.Embedded;

        var response = AppCommandCaller.VersionResponse(Id, manifest, "26.1.2.3");

        Assert.Equal(AppCommandExit.Done, response.ExitCode);
        Assert.Equal(manifest.Product, response.Data!["product"]!.GetValue<string>());
        Assert.Equal("26.1.2.3", response.Data["version"]!.GetValue<string>());
        Assert.Equal(manifest.CliVersion, response.Data["cliVersion"]!.GetValue<string>());
    }

    [Fact]
    public void TheCommandListNamesEveryCommandAndTheExecutableTheReaderRuns()
    {
        var manifest = AppCommandManifest.Embedded;

        var list = AppCommandCaller.CommandList(manifest, "Tool.exe");

        Assert.All(manifest.Commands, command => Assert.Contains(command.Summary, list, StringComparison.Ordinal));
        Assert.EndsWith("Run \"Tool.exe commands --json\" for the manifest itself.", list, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestCarriesTheVerbItsWordsAndWhereAndWhoItCameFrom()
    {
        var entry = AppCommandManifest.Embedded.Find("volume set")!;

        var request = AppCommandCaller.NewRequest(entry, ["--level", "30"], Id);

        Assert.Equal("volume set", request.Command);
        Assert.Equal(new[] { "--level", "30" }, request.Args);
        Assert.Equal(Id, request.RequestId);
        Assert.Equal(Environment.CurrentDirectory, request.WorkingDir);
        Assert.Equal(Environment.ProcessId, request.CallerPid);
    }

    [Fact]
    public void TheOutputRuleSaysTheMessageOrTheJsonLineAndReturnsTheExitCode()
    {
        var lines = new List<string>();
        var complaints = new List<string>();
        var response = AppCommandResponse.Done(Id, "all good");

        var plain = AppCommandOutput.Report(response, json: false, resultPath: null, lines.Add, complaints.Add);
        var json = AppCommandOutput.Report(response, json: true, resultPath: null, lines.Add, complaints.Add);

        Assert.Equal(AppCommandExit.Done, plain);
        Assert.Equal(AppCommandExit.Done, json);
        Assert.Equal("all good", lines[0]);
        Assert.Equal(response.ToJsonLine(), lines[1]);
        Assert.Empty(complaints);
    }

    [Fact]
    public void AResultIsWrittenOnlyForAFinishedCommandAndWholeOrNotAtAll()
    {
        var path = Path.Combine(Path.GetTempPath(), "sp0275-output-" + Guid.NewGuid().ToString("N") + ".json");
        var complaints = new List<string>();
        try
        {
            var refused = AppCommandOutput.Report(
                AppCommandPolicy.NotRunning(Id), json: false, path, _ => { }, complaints.Add);
            Assert.Equal(AppCommandExit.NotReady, refused);
            Assert.False(File.Exists(path));

            var done = AppCommandOutput.Report(
                AppCommandResponse.Done(Id, "ok", new JsonObject { ["n"] = 7 }), json: false, path, _ => { }, complaints.Add);
            Assert.Equal(AppCommandExit.Done, done);
            Assert.Equal(7, JsonNode.Parse(File.ReadAllText(path))!["n"]!.GetValue<int>());

            var unwritable = AppCommandOutput.Report(
                AppCommandResponse.Done(Id, "ok"), json: false, Path.Combine(path, "inside-a-file.json"), _ => { }, complaints.Add);
            Assert.Equal(AppCommandExit.Io, unwritable);
            Assert.Equal(AppCommandOutput.ResultNotWritten, Assert.Single(complaints));
            Assert.Equal(7, JsonNode.Parse(File.ReadAllText(path))!["n"]!.GetValue<int>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheProfileNamesTheFrozenLockAndPipeOfThisSession()
    {
        Assert.Equal("STREAMSPLAYER_DATA_DIRECTORY", AppDataProfile.Variable);
        Assert.EndsWith("StreamsPlayer", AppDataProfile.DefaultDirectory, StringComparison.Ordinal);
        var identity = AppDataProfile.Identity();
        Assert.StartsWith(SingleInstanceIdentity.ProductMutexName, identity.MutexName, StringComparison.Ordinal);
        Assert.StartsWith(SingleInstanceIdentity.ProductPipeName + "-s", identity.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildVersionOfAnAssemblyWithoutAStampIsUnknown()
    {
        Assert.Equal("Unknown", BuildVersion.Of(null));
        Assert.Matches(@"^[^+]+$", BuildVersion.Of(typeof(AppCommandCaller).Assembly));
    }

    private static AppCommandResponse Unexpected() => throw new InvalidOperationException("The older-instance branch was not expected.");
}
