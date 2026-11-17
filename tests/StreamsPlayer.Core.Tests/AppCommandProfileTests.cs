using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0271: the APP-COMMANDS rule 7 profile against an in-process listener - cases C4 to C7, the older first instance,
// and the frozen launch line answered as before beside it.
public sealed class AppCommandProfileTests
{
    // Waits that only guard against a hung test: the pipe appears and the answer arrives in milliseconds, and a loaded
    // machine has taken seconds. The tests that prove a bound pass their own short one.
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Completion = TimeSpan.FromSeconds(30);

    // How long a call that the product must cut short may take before the test calls the product's bound broken. The
    // stalls below last a minute or never end, so a bound that does not work cannot meet it.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AForwardedCommandGetsItsAnswerOnTheSameConnection()
    {
        await using var harness = Harness.Start(Answer);

        var request = AppCommandRequest.Create("status", ["--json"]);
        var exchange = AppCommandClient.Send(harness.PipeName, request, Connect, Completion);

        Assert.Equal(AppCommandDelivery.Answered, exchange.Delivery);
        Assert.Equal(request.RequestId, exchange.Response!.RequestId);
        Assert.Equal(AppCommandExit.Done, exchange.Response.ExitCode);
        Assert.True(exchange.Response.Data!["ready"]!.GetValue<bool>());
        Assert.Equal("status", Assert.Single(harness.Commands).Command);
    }

    [Fact]
    public async Task AnUnknownVerbAndAnUnknownOptionExecuteNothingAndStatusStillAnswers()
    {
        await using var harness = Harness.Start(Answer);

        var unknownVerb = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("frobnicate", []), Connect, Completion);
        var unknownOption = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", ["--bogus"]), Connect, Completion);
        var status = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion);

        Assert.Equal(AppCommandExit.Usage, unknownVerb.Response!.ExitCode);
        Assert.Equal(AppCommandReason.UnknownVerb, unknownVerb.Response.Reason);
        Assert.Equal(AppCommandExit.Usage, unknownOption.Response!.ExitCode);
        Assert.Equal(AppCommandReason.BadArgument, unknownOption.Response.Reason);
        Assert.Equal(AppCommandExit.Done, status.Response!.ExitCode);
        Assert.Equal(1, harness.Executed);
    }

    [Fact]
    public async Task ABusyFirstInstanceAnswersBusyExit11()
    {
        await using var harness = Harness.Start((request, _) =>
            Task.FromResult(AppCommandResponse.Busy(request.RequestId, AppCommandMessages.Busy)));

        var exchange = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion);

        Assert.Equal(AppCommandExit.NotReady, exchange.Response!.ExitCode);
        Assert.Equal(AppCommandReason.Busy, exchange.Response.Reason);
    }

    [Fact]
    public async Task AClosingListenerAnswersBusyWithoutAskingTheHandler()
    {
        await using var harness = Harness.Start(Answer);
        harness.Listener.StopAccepting();

        var exchange = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion);

        Assert.Equal(AppCommandStatus.Busy, exchange.Response!.Status);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task AVerbPastItsBoundIsNeverReportedDone()
    {
        await using var harness = Harness.Start(async (request, cancellation) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellation);
            return AppCommandResponse.Done(request.RequestId, "late");
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var exchange = await Task.Run(() => AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect,
            TimeSpan.FromMilliseconds(300))).WaitAsync(Limit);

        Assert.Equal(AppCommandDelivery.NoAnswer, exchange.Delivery);
        Assert.True(clock.Elapsed < Limit, $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task AReceiverThatHoldsThePipeNeverHangsTheCaller()
    {
        var pipeName = "sp-test-hold-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connected = server.WaitForConnectionAsync();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var send = Task.Run(() => AppCommandClient.Send(pipeName, AppCommandRequest.Create("status", []), Connect,
            TimeSpan.FromMilliseconds(500)));
        await connected.WaitAsync(Limit);
        var exchange = await send.WaitAsync(Limit);

        Assert.Equal(AppCommandDelivery.NoAnswer, exchange.Delivery);
        Assert.True(clock.Elapsed < Limit, $"waited {clock.Elapsed}");
    }

    [Fact]
    public void NothingListeningIsNotConnected()
    {
        var exchange = AppCommandClient.Send("sp-test-absent-" + Guid.NewGuid().ToString("N"), AppCommandRequest.Create("status", []),
            TimeSpan.FromMilliseconds(200), Completion);

        Assert.Equal(AppCommandDelivery.NotConnected, exchange.Delivery);
    }

    [Fact]
    public async Task AnOlderFirstInstanceAnswersWithItsPlainLine()
    {
        var requests = new BlockingCollection<IReadOnlyList<string>>();
        var pipeName = "sp-test-old-" + Guid.NewGuid().ToString("N");
        await using var older = new ActivationPipeListener(pipeName, requests.Add, (_, _) => { });
        older.Start();
        NamedPipeReadiness.WaitUntilListed(pipeName);

        var exchange = AppCommandClient.Send(pipeName, AppCommandRequest.Create("status", []), Connect, Completion);

        Assert.Equal(AppCommandDelivery.LegacyReply, exchange.Delivery);
        Assert.Equal(ActivationPipeListener.RefusedReply, exchange.LegacyReply);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task TheFrozenLaunchLineIsStillAnsweredOkBesideTheProfile()
    {
        await using var harness = Harness.Start(Answer);

        Assert.Equal(ActivationSendResult.Delivered,
            ActivationPipeClient.TrySend(harness.PipeName, ["--url", "https://example.test/a"], Connect));

        Assert.Equal(["--url", "https://example.test/a"], await harness.NextLaunchAsync());
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task ARequestOfANewerSchemaIsRefusedWithExit2()
    {
        await using var harness = Harness.Start(Answer);

        var reply = await SendRawAsync(harness.PipeName,
            "{\"schemaVersion\":2,\"command\":\"status\",\"args\":[],\"requestId\":\"r-1\"}\n");

        Assert.True(AppCommandResponse.TryParse(reply, out var response), reply);
        Assert.Equal("r-1", response.RequestId);
        Assert.Equal(AppCommandExit.NotProven, response.ExitCode);
        Assert.Equal(AppCommandReason.Schema, response.Reason);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task AHandlerThatThrowsIsAnsweredFailedAndTheListenerGoesOn()
    {
        var calls = 0;
        await using var harness = Harness.Start((request, cancellation) =>
            Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : Answer(request, cancellation));

        var failed = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion);
        var next = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion);

        Assert.Equal(AppCommandExit.Failed, failed.Response!.ExitCode);
        Assert.Equal(AppCommandStatus.Failed, failed.Response.Status);
        Assert.Equal(AppCommandExit.Done, next.Response!.ExitCode);
    }

    // ---- SP-0279: the answers the contract asks for on the pipe -------------------------------------------------------------

    // Item I.3, case C19: a request line over the bound is answered refused / too-large / exit 10 with an empty request id,
    // nothing is executed, and the listener goes on.
    [Fact]
    public async Task AnOversizedRequestLineIsAnsweredTooLargeWithAnEmptyRequestId()
    {
        await using var harness = Harness.Start(Answer);

        var reply = await SendRawAsync(harness.PipeName,
            new string('a', AppCommandLimits.MaximumRequestBytes + 100) + "\n");

        Assert.True(AppCommandResponse.TryParse(reply, out var response), reply);
        Assert.Equal(string.Empty, response.RequestId);
        Assert.Equal(AppCommandStatus.Refused, response.Status);
        Assert.Equal(AppCommandReason.TooLarge, response.Reason);
        Assert.Equal(AppCommandExit.Usage, response.ExitCode);
        Assert.Empty(harness.Commands);

        var next = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion);
        Assert.Equal(AppCommandDelivery.Answered, next.Delivery);
    }

    // Items I.3 and K: the second process that reads that answer, or any answer that is not to its request, never takes it
    // for its own and never exits with its code.
    [Fact]
    public async Task AnAnswerToAnotherRequestIsUnreadableForTheSecondProcess()
    {
        await using var harness = Harness.Start((_, _) => Task.FromResult(AppCommandResponse.Done("someone-else", "not yours")));

        var request = AppCommandRequest.Create("status", []);
        var exchange = AppCommandClient.Send(harness.PipeName, request, Connect, Completion);

        Assert.Equal(AppCommandDelivery.Unreadable, exchange.Delivery);
        var response = AppCommandCaller.Interpret(exchange, SingleInstanceLockResult.HeldByAnotherCopy, request.RequestId,
            () => throw new InvalidOperationException("an unreadable answer is not an older copy"));
        Assert.Equal(AppCommandExit.NotProven, response.ExitCode);
        Assert.Equal(request.RequestId, response.RequestId);
    }

    // Item L, case C17: a response line over 64 KiB is replaced by failed / response-too-large / exit 1, carrying the
    // request's own id so the second process can still tell whose answer it is.
    [Fact]
    public async Task AResponseOverTheBoundIsReplacedByResponseTooLarge()
    {
        await using var harness = Harness.Start((request, _) => Task.FromResult(AppCommandResponse.Done(
            request.RequestId, "big", new System.Text.Json.Nodes.JsonObject { ["blob"] = new string('x', AppCommandLimits.MaximumResponseBytes) })));

        var request = AppCommandRequest.Create("status", []);
        var exchange = AppCommandClient.Send(harness.PipeName, request, Connect, Completion);

        Assert.Equal(AppCommandDelivery.Answered, exchange.Delivery);
        Assert.Equal(request.RequestId, exchange.Response!.RequestId);
        Assert.Equal(AppCommandStatus.Failed, exchange.Response.Status);
        Assert.Equal(AppCommandReason.ResponseTooLarge, exchange.Response.Reason);
        Assert.Equal(AppCommandExit.Failed, exchange.Response.ExitCode);
        Assert.Null(exchange.Response.Data);
    }

    [Fact]
    public async Task AnOrdinaryResponseIsNotTouchedByTheBound()
    {
        await using var harness = Harness.Start(Answer);

        var exchange = AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion);

        Assert.Equal(AppCommandDelivery.Answered, exchange.Delivery);
        Assert.Null(exchange.Response!.Reason);
    }

    // Item K: a listener that is closing while a command runs answers busy, because going quiet now reads as an older copy.
    [Fact]
    public async Task AListenerThatIsStoppingUnderARunningCommandAnswersBusy()
    {
        var harness = Harness.Start(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return AppCommandResponse.Done("unreachable", "never");
        });

        var send = Task.Run(() => AppCommandClient.Send(harness.PipeName, AppCommandRequest.Create("status", []), Connect, Completion));
        for (var wait = 0; wait < 600 && harness.Commands.IsEmpty; wait++)
        {
            await Task.Delay(50);
        }

        Assert.False(harness.Commands.IsEmpty, "the handler did not start");
        await harness.DisposeAsync();
        var exchange = await send;

        Assert.Equal(AppCommandDelivery.Answered, exchange.Delivery);
        Assert.Equal(AppCommandStatus.Busy, exchange.Response!.Status);
        Assert.Equal(AppCommandExit.NotReady, exchange.Response.ExitCode);
    }

    // Item K: a receiver that takes the request and hangs up with no line is the evidence of an older copy.
    [Fact]
    public async Task AReceiverThatHangsUpWithNoLineIsClosedNotNoAnswer()
    {
        var pipeName = "sp-test-hangup-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var served = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await server.ReadAsync(new byte[4096]);
            server.Dispose();
        });

        var exchange = AppCommandClient.Send(pipeName, AppCommandRequest.Create("status", []), Connect, Completion);
        await served;

        Assert.Equal(AppCommandDelivery.Closed, exchange.Delivery);
    }

    // Item J: the first instance's start bound is shorter than the delivery bound the second process counts from the accepted
    // connection, so its busy answer arrives inside that bound.
    [Fact]
    public void TheStartBoundOfTheFirstInstanceIsShorterThanTheDeliveryBound()
    {
        Assert.True(AppCommandLimits.StartBound > TimeSpan.Zero);
        Assert.True(AppCommandLimits.StartBound < AppCommandClient.DeliveryBound);
    }

    /// <summary>The first instance's side as the application does it: the same parser, then the verb.</summary>
    private static Task<AppCommandResponse> Answer(AppCommandRequest request, CancellationToken cancellation)
    {
        var parse = AppCommandLine.Parse([.. request.Command.Split(' '), .. request.Args], AppCommandManifest.Embedded);
        return Task.FromResult(parse.Kind == AppCommandParseKind.Verb
            ? AppCommandResponse.Done(request.RequestId, "ready", new System.Text.Json.Nodes.JsonObject { ["ready"] = true })
            : parse.ToRefusal(request.RequestId));
    }

    private static async Task<string> SendRawAsync(string pipeName, string line)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync((int)Connect.TotalMilliseconds);
        await client.WriteAsync(Encoding.UTF8.GetBytes(line));
        await client.FlushAsync();
        using var reader = new StreamReader(client, Encoding.UTF8);
        using var timeout = new CancellationTokenSource(Completion);
        return await reader.ReadLineAsync(timeout.Token) ?? string.Empty;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly BlockingCollection<IReadOnlyList<string>> _launches = new();
        private int _executed;

        private Harness(Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>> handler)
        {
            PipeName = "sp-test-cmd-" + Guid.NewGuid().ToString("N");
            Listener = new ActivationPipeListener(PipeName, _launches.Add, (_, _) => { }, async (request, cancellation) =>
            {
                Commands.Enqueue(request);
                var response = await handler(request, cancellation);
                if (response.ExitCode == AppCommandExit.Done)
                {
                    Interlocked.Increment(ref _executed);
                }

                return response;
            });
        }

        public string PipeName { get; }

        public ActivationPipeListener Listener { get; }

        public ConcurrentQueue<AppCommandRequest> Commands { get; } = new();

        public int Executed => Volatile.Read(ref _executed);

        public static Harness Start(Func<AppCommandRequest, CancellationToken, Task<AppCommandResponse>> handler)
        {
            var harness = new Harness(handler);
            harness.Listener.Start();
            NamedPipeReadiness.WaitUntilListed(harness.PipeName);
            return harness;
        }

        public Task<IReadOnlyList<string>> NextLaunchAsync() => Task.Run(() =>
            _launches.TryTake(out var launch, Completion) ? launch : throw new TimeoutException("no launch delivered"));

        public async ValueTask DisposeAsync()
        {
            await Listener.DisposeAsync();
            _launches.Dispose();
        }
    }
}
