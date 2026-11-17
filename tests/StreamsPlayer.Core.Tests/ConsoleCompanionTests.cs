using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0275: the console companion of APP-COMMANDS rule 9, run through its whole path - the same AppCommandConsole.Run the
// executable's Main calls - against an in-process first instance (the pipe listener of the profile plus a lock held on a
// thread of its own, which is how a running copy looks from outside).
public sealed class ConsoleCompanionTests
{
    private const string Version = "26.1.2.3";

    [Fact]
    public void CommandsJsonPrintsTheShippedManifestBytesWithNoFirstInstance()
    {
        var run = Run(["commands", "--json"], NewIdentity());

        Assert.Equal(AppCommandExit.Done, run.Exit);
        Assert.Equal(AppCommandManifest.EmbeddedBytes.ToArray(), run.Output);
        Assert.Equal(string.Empty, run.Errors);
    }

    [Fact]
    public void CommandsWithoutJsonPrintsTheListAndPointsAtTheCompanion()
    {
        var run = Run(["commands"], NewIdentity());

        Assert.Equal(AppCommandExit.Done, run.Exit);
        Assert.Contains("STREAMS Player commands", run.Text, StringComparison.Ordinal);
        Assert.All(AppCommandManifest.Embedded.Commands, command => Assert.Contains(command.Name, run.Text, StringComparison.Ordinal));
        Assert.Contains("StreamsPlayerCli.exe commands --json", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionPrintsTheStampItWasGivenAndExitsZero()
    {
        var run = Run(["version", "--json"], NewIdentity());

        Assert.Equal(AppCommandExit.Done, run.Exit);
        var line = JsonNode.Parse(run.Text)!;
        Assert.Equal("done", line["status"]!.GetValue<string>());
        Assert.Equal(Version, line["data"]!["version"]!.GetValue<string>());
        Assert.Equal(AppCommandManifest.Embedded.CliVersion, line["data"]!["cliVersion"]!.GetValue<string>());
    }

    [Fact]
    public void StatusWithNothingRunningExits11NotRunning()
    {
        var run = Run(["status", "--json"], NewIdentity());

        Assert.Equal(AppCommandExit.NotReady, run.Exit);
        Assert.Equal(AppCommandReason.NotRunning, JsonNode.Parse(run.Text)!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task StatusWithAFirstInstanceExits0AndPrintsItsAnswer()
    {
        await using var first = new FirstInstance((request, _) =>
            AppCommandResponse.Done(request.RequestId, "ready", new JsonObject { ["ready"] = true }));

        var run = Run(["status", "--json"], first.Identity);

        Assert.Equal(AppCommandExit.Done, run.Exit);
        Assert.True(JsonNode.Parse(run.Text)!["data"]!["ready"]!.GetValue<bool>());
        Assert.Equal("status", Assert.Single(first.Commands).Command);
    }

    [Fact]
    public async Task AnArgumentTravelsToTheFirstInstanceAsTyped()
    {
        await using var first = new FirstInstance((request, _) => AppCommandResponse.Done(request.RequestId, "set"));

        var run = Run(["volume", "set", "--level", "30"], first.Identity);

        Assert.Equal(AppCommandExit.Done, run.Exit);
        var request = Assert.Single(first.Commands);
        Assert.Equal("volume set", request.Command);
        Assert.Equal(new[] { "--level", "30" }, request.Args);
        Assert.Equal(AppCommandRequest.CliOrigin, request.Origin);
    }

    [Fact]
    public async Task ABusyFirstInstanceIsExit11()
    {
        await using var first = new FirstInstance((request, _) => AppCommandResponse.Busy(request.RequestId, AppCommandMessages.Busy));

        var run = Run(["status"], first.Identity);

        Assert.Equal(AppCommandExit.NotReady, run.Exit);
        Assert.Contains("busy", run.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("playback pause")]
    [InlineData("playback resume")]
    [InlineData("playback status")]
    public void ARefuseVerbWithNothingRunningExits11(string verb)
    {
        var run = Run(verb.Split(' '), NewIdentity());

        Assert.Equal(AppCommandExit.NotReady, run.Exit);
        Assert.Equal(AppCommandMessages.NotRunning, run.Text);
    }

    [Fact]
    public void QuitWithNothingRunningIsDoneExit0()
    {
        var run = Run(["quit"], NewIdentity());

        Assert.Equal(AppCommandExit.Done, run.Exit);
        Assert.Contains("nothing to close", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackStopWithNothingRunningIsDoneAndSaysNothingPlayed()
    {
        var run = Run(["playback", "stop", "--all"], NewIdentity());

        Assert.Equal(AppCommandExit.Done, run.Exit);
        Assert.Equal(AppPlaybackMessages.NothingPlaying, run.Text);
    }

    [Fact]
    public async Task QuitWaitsForTheFirstInstanceToLetGoOfItsLockThenExits0()
    {
        await using var first = new FirstInstance((request, instance) =>
        {
            instance.ReleaseLock();
            return AppCommandResponse.Done(request.RequestId, AppCommandMessages.Closing);
        });

        var run = Run(["quit"], first.Identity);

        Assert.Equal(AppCommandExit.Done, run.Exit);
        Assert.Contains("has closed", run.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("play --url https://example.test/live.mp3")]
    [InlineData("channel add --url https://example.test/live.mp3")]
    public void AVerbThePlayersOwnExecutableServesIsRefusedExit10AndNamedThere(string line)
    {
        var words = line.Split(' ');
        var name = string.Join(' ', words.TakeWhile(word => !word.StartsWith("--", StringComparison.Ordinal)));
        var run = Run(words, NewIdentity());

        Assert.Equal(AppCommandExit.Usage, run.Exit);
        Assert.Equal(AppCommandMessages.NotServedHere(name), run.Text);
        Assert.Contains($"StreamsPlayer.exe {name}", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalOfAPlayerVerbIsTheUnknownVerbReason()
    {
        var run = Run(["play", "--url", "https://example.test/live.mp3", "--json"], NewIdentity());

        Assert.Equal(AppCommandExit.Usage, run.Exit);
        var answer = JsonNode.Parse(run.Text)!;
        Assert.Equal("refused", answer["status"]!.GetValue<string>());
        Assert.Equal(AppCommandReason.UnknownVerb, answer["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AVerbThePlayerServesNeverReachesAFirstInstanceFromTheCompanion()
    {
        await using var first = new FirstInstance((request, _) => AppCommandResponse.Done(request.RequestId, "shown"));

        var run = Run(["show"], first.Identity);

        Assert.Equal(AppCommandExit.Usage, run.Exit);
        Assert.Empty(first.Commands);
    }

    [Fact]
    public void ALaunchSpellingIsTheSameRefusalAsPlay()
    {
        var run = Run(["--url", "https://example.test/live.mp3"], NewIdentity());

        Assert.Equal(AppCommandExit.Usage, run.Exit);
        Assert.Contains("StreamsPlayer.exe play", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommandPrintsTheUsageAndExits10()
    {
        var run = Run([], NewIdentity());

        Assert.Equal(AppCommandExit.Usage, run.Exit);
        Assert.Contains("Usage: StreamsPlayerCli.exe", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVerbAndAnUnknownOptionAreExit10AndAFollowingStatusStillAnswers()
    {
        var identity = NewIdentity();

        var verb = Run(["frobnicate", "--json"], identity);
        var option = Run(["status", "--bogus", "--json"], identity);
        var status = Run(["status"], identity);

        Assert.Equal(AppCommandExit.Usage, verb.Exit);
        Assert.Equal(AppCommandReason.UnknownVerb, JsonNode.Parse(verb.Text)!["reason"]!.GetValue<string>());
        Assert.Equal(AppCommandExit.Usage, option.Exit);
        Assert.Equal(AppCommandReason.BadArgument, JsonNode.Parse(option.Text)!["reason"]!.GetValue<string>());
        Assert.Equal(AppCommandExit.NotReady, status.Exit);
    }

    [Fact]
    public void TheResultFileIsWrittenWholeAndTheAnswerStillPrints()
    {
        var path = Path.Combine(Path.GetTempPath(), "sp0275-result-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var run = Run(["playback", "stop", "--all", "--result", path], NewIdentity());

            Assert.Equal(AppCommandExit.Done, run.Exit);
            Assert.Equal(AppPlaybackMessages.NothingPlaying, run.Text);
            Assert.Equal(0, JsonNode.Parse(File.ReadAllText(path))!["stopped"]!.GetValue<int>());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AResultPathThatCannotBeWrittenIsExit14AndSaysSo()
    {
        var missingFolder = Path.Combine(Path.GetTempPath(), "sp0275-missing-" + Guid.NewGuid().ToString("N"), "deeper", "r.json");

        var run = Run(["playback", "stop", "--all", "--result", missingFolder], NewIdentity());

        Assert.Equal(AppCommandExit.Io, run.Exit);
        Assert.Contains("--result", run.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCommandTheManifestHandsToTheCompanionIsServedAndTheOthersAreNot()
    {
        var manifest = AppCommandManifest.Embedded;

        Assert.All(manifest.Commands, command =>
            Assert.Equal(command.ColdStart != "start", AppCommandConsole.Serves(manifest, command)));
        Assert.Equal(AppCommandConsole.ExecutableName, manifest.Find("status")!.Exe);
    }

    private static SingleInstanceIdentity NewIdentity() =>
        new(@"Local\sp-test-cli-lock-" + Guid.NewGuid().ToString("N"), "sp-test-cli-pipe-" + Guid.NewGuid().ToString("N"));

    private static Outcome Run(string[] args, SingleInstanceIdentity identity)
    {
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        var exit = AppCommandConsole.Run(args, identity, Version, output, errors);
        var bytes = output.ToArray();
        return new Outcome(exit, bytes, Encoding.UTF8.GetString(bytes).Trim(), Encoding.UTF8.GetString(errors.ToArray()).Trim());
    }

    private sealed record Outcome(int Exit, byte[] Output, string Text, string Errors);

    /// <summary>
    /// A mutex is owned by the thread that took it and a thread may take its own mutex again, so the lock is held on a thread of
    /// its own - the way a running copy holds it from the companion's point of view.
    /// </summary>
    private sealed class LockHolder : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public LockHolder(string mutexName)
        {
            using var ready = new ManualResetEventSlim();
            var acquired = false;
            _thread = new Thread(() =>
            {
                acquired = SingleInstanceLock.TryAcquire(mutexName, TimeSpan.Zero, out var held) == SingleInstanceLockResult.Acquired;
                ready.Set();
                if (!acquired)
                {
                    return;
                }

                _release.Wait();
                held!.Dispose();
            })
            {
                IsBackground = true
            };
            _thread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "the lock holder did not start");
            Assert.True(acquired, "the lock holder could not take the lock");
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(30));
            _release.Dispose();
        }
    }

    /// <summary>A first instance as the companion meets it: a held lock and a listener on the frozen kind of pipe.</summary>
    private sealed class FirstInstance : IAsyncDisposable
    {
        private readonly ActivationPipeListener _listener;
        private readonly LockHolder _lock;

        public FirstInstance(Func<AppCommandRequest, FirstInstance, AppCommandResponse> answer)
        {
            Identity = NewIdentity();
            _lock = new LockHolder(Identity.MutexName);
            _listener = new ActivationPipeListener(Identity.PipeName, _ => { }, (_, _) => { }, (request, _) =>
            {
                Commands.Enqueue(request);
                return Task.FromResult(answer(request, this));
            });
            _listener.Start();

            // Start() only schedules the accept loop. The companion gives a pipe one second to appear and answers 11 when
            // it does not, so the test must not begin before the listener has really created its pipe.
            NamedPipeReadiness.WaitUntilListed(Identity.PipeName);
        }

        public SingleInstanceIdentity Identity { get; }

        public ConcurrentQueue<AppCommandRequest> Commands { get; } = new();

        public void ReleaseLock() => _lock.Release();

        public async ValueTask DisposeAsync()
        {
            await _listener.DisposeAsync();
            _lock.Dispose();
        }
    }
}
