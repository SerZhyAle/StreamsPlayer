using System.Collections.Concurrent;
using System.IO.Pipes;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

// SP-0118: APP-ACTIVATION rule 3 - no connection, however broken, stops the listener; disposal does.
public sealed class ActivationPipeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Listener_DeliversAForwardedLaunch()
    {
        await using var harness = Harness.Start();

        Assert.True(ActivationPipeClient.TrySend(harness.PipeName, ["--url", "https://example.test/a"], Wait));

        Assert.Equal(["--url", "https://example.test/a"], await harness.NextRequestAsync());
    }

    [Fact]
    public async Task Listener_SurvivesBrokenConnectionsAndKeepsServing()
    {
        await using var harness = Harness.Start();

        // A zero-byte connection, garbage, an oversized line and a client that sends half a line and drops.
        await SendRawAsync(harness.PipeName, []);
        await SendRawAsync(harness.PipeName, "garbage\n"u8.ToArray());
        await SendRawAsync(harness.PipeName, new byte[ActivationMessage.MaximumPayloadBytes + 10]);
        await SendRawAsync(harness.PipeName, "{\"schemaVersion\":1,\"comm"u8.ToArray());

        Assert.True(ActivationPipeClient.TrySend(harness.PipeName, ["--id", "0f8fad5b-d9cb-469f-a165-70867728950e"], Wait));

        Assert.Equal(["--id", "0f8fad5b-d9cb-469f-a165-70867728950e"], await harness.NextRequestAsync());
        Assert.True(harness.Rejections.Count >= 4, $"rejections: {string.Join(", ", harness.Rejections)}");
    }

    [Fact]
    public async Task Listener_DisposesCleanlyWhileWaiting()
    {
        var harness = Harness.Start();

        var dispose = harness.DisposeAsync().AsTask();

        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(Wait)));
        await dispose;
    }

    [Fact]
    public void Client_ReportsFailureWhenNothingIsListening()
    {
        var pipeName = "sp-test-absent-" + Guid.NewGuid().ToString("N");

        Assert.False(ActivationPipeClient.TrySend(pipeName, [], TimeSpan.FromMilliseconds(200)));
    }

    private static async Task SendRawAsync(string pipeName, byte[] bytes)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        await client.ConnectAsync((int)Wait.TotalMilliseconds);
        if (bytes.Length > 0)
        {
            try
            {
                await client.WriteAsync(bytes);
            }
            catch (IOException)
            {
                // The listener may drop an oversized line before it is fully written.
            }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly BlockingCollection<IReadOnlyList<string>> _requests = new();
        private readonly ActivationPipeListener _listener;

        private Harness()
        {
            PipeName = "sp-test-" + Guid.NewGuid().ToString("N");
            _listener = new ActivationPipeListener(PipeName, _requests.Add, (reason, _) => Rejections.Enqueue(reason));
        }

        public string PipeName { get; }

        public ConcurrentQueue<string> Rejections { get; } = new();

        public static Harness Start()
        {
            var harness = new Harness();
            harness._listener.Start();
            return harness;
        }

        public Task<IReadOnlyList<string>> NextRequestAsync() => Task.Run(() =>
            _requests.TryTake(out var request, Wait) ? request : throw new TimeoutException("no request delivered"));

        public async ValueTask DisposeAsync()
        {
            await _listener.DisposeAsync();
            _requests.Dispose();
        }
    }
}
