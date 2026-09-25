using System.IO.Pipes;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0118: the running copy's end of the <c>APP-ACTIVATION</c> rule 3 pipe. Accepts one connection at
/// a time, reads one <see cref="ActivationMessage"/> line from it, and hands the arguments on.
/// </summary>
/// <remarks>
/// <para>
/// Every connection runs inside its own failure boundary: an empty, malformed, oversized, stalled or
/// abruptly closed connection is reported through <c>onRejected</c> and the loop goes on to the next
/// one. Only disposal ends it - the pending asynchronous wait is cancelled, so no nudge connection is
/// needed to unblock it.
/// </para>
/// <para>
/// The pipe is opened with <see cref="PipeOptions.CurrentUserOnly"/>, and its name carries the session
/// id (<see cref="SingleInstanceIdentity"/>), so only the current user's own session can reach it.
/// The callbacks run on a pool thread; a UI consumer marshals them itself.
/// </para>
/// </remarks>
public sealed class ActivationPipeListener : IAsyncDisposable
{
    /// <summary>How long a connected client has to deliver its line before it is dropped.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly Action<IReadOnlyList<string>> _onRequest;
    private readonly Action<string, Exception?> _onRejected;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public ActivationPipeListener(
        string pipeName,
        Action<IReadOnlyList<string>> onRequest,
        Action<string, Exception?> onRejected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));
        _onRejected = onRejected ?? throw new ArgumentNullException(nameof(onRejected));
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception exception)
            {
                Reject("pipe could not be opened", exception);
                if (!await DelayAsync(ReopenDelay, stopping).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Reject("connection failed", exception);
                    continue;
                }

                await ServeAsync(server, stopping).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(ReadTimeout);
        try
        {
            var line = await ReadLineAsync(server, deadline.Token).ConfigureAwait(false);
            if (line is null)
            {
                Reject("payload exceeded the size limit", null);
            }
            else if (line.Length == 0)
            {
                Reject("empty connection", null);
            }
            else if (ActivationMessage.TryParse(line, out var arguments))
            {
                _onRequest(arguments);
            }
            else
            {
                Reject("malformed payload", null);
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            Reject("client sent nothing within the read timeout", null);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the connection is abandoned with the listener.
        }
        catch (Exception exception)
        {
            Reject("connection aborted", exception);
        }
    }

    /// <summary>
    /// The bytes up to the first newline or the end of the stream; <see langword="null"/> when that is
    /// more than <see cref="ActivationMessage.MaximumPayloadBytes"/>.
    /// </summary>
    private static async Task<byte[]?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.ToArray();
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            line.Write(buffer, 0, newline < 0 ? read : newline);
            if (line.Length > ActivationMessage.MaximumPayloadBytes)
            {
                return null;
            }

            if (newline >= 0)
            {
                return line.ToArray();
            }
        }
    }

    private void Reject(string reason, Exception? exception)
    {
        try
        {
            _onRejected(reason, exception);
        }
        catch
        {
            // A failing diagnostic sink must not be the thing that stops the listener.
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stopping)
    {
        try
        {
            await Task.Delay(delay, stopping).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>SP-0118: a later copy's end of the pipe.</summary>
public static class ActivationPipeClient
{
    /// <summary>The contract's bounded connect timeout (rule 3 allows 500-1000 ms).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Delivers <paramref name="arguments"/> to the copy listening on <paramref name="pipeName"/>.
    /// <see langword="false"/> when nothing accepted the connection within <paramref name="timeout"/>
    /// or the write failed; never throws for a pipe fault.
    /// </summary>
    public static bool TrySend(string pipeName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var payload = ActivationMessage.Serialize(arguments);
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout);
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
