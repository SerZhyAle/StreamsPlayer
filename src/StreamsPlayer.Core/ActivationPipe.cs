using System.IO.Pipes;
using System.Text;

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
/// <para>
/// SP-0170: every line read is answered with one acknowledgement line - <see cref="AcceptedReply"/> once the
/// request has been handed on, <see cref="RefusedReply"/> for anything the listener will not act on,
/// including every request after <see cref="StopAccepting"/>. The sender therefore knows whether its launch
/// was taken, instead of counting a flushed write as delivery.
/// </para>
/// </remarks>
public sealed class ActivationPipeListener : IAsyncDisposable
{
    /// <summary>The acknowledgement line for a request that was handed on.</summary>
    public const string AcceptedReply = "ok";

    /// <summary>The acknowledgement line for a request the listener will not act on.</summary>
    public const string RefusedReply = "refused";

    /// <summary>How long a connected client has to deliver its line before it is dropped.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly Action<IReadOnlyList<string>> _onRequest;
    private readonly Action<string, Exception?> _onRejected;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile bool _accepting = true;

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

    /// <summary>
    /// SP-0170: from now on every well-formed request is answered <see cref="RefusedReply"/> and not handed
    /// on. The pipe stays open so a later copy learns at once that this one is closing, instead of timing out.
    /// </summary>
    public void StopAccepting() => _accepting = false;

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
                    PipeDirection.InOut,
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
            var accepted = false;
            if (line is null)
            {
                Reject("payload exceeded the size limit", null);
            }
            else if (line.Length == 0)
            {
                Reject("empty connection", null);
            }
            else if (!ActivationMessage.TryParse(line, out var arguments))
            {
                Reject("malformed payload", null);
            }
            else if (!_accepting)
            {
                Reject("listener is closing", null);
            }
            else
            {
                _onRequest(arguments);
                accepted = true;
            }

            if (line is { Length: > 0 })
            {
                await AcknowledgeAsync(server, accepted, deadline.Token).ConfigureAwait(false);
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
    /// SP-0170: writes the verdict, then waits for the sender to hang up - closing first could discard a reply
    /// the sender has not read yet. A sender that is already gone (an older copy never reads) is not a fault.
    /// </summary>
    private static async Task AcknowledgeAsync(NamedPipeServerStream server, bool accepted, CancellationToken cancellationToken)
    {
        try
        {
            var reply = Encoding.ASCII.GetBytes((accepted ? AcceptedReply : RefusedReply) + "\n");
            await server.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
            await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[16];
            while (await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
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

/// <summary>SP-0170: how a forwarded launch ended, from the sending copy's side.</summary>
public enum ActivationSendResult
{
    /// <summary>The running copy took the launch and said so.</summary>
    Delivered,

    /// <summary>The launch is beyond what the receiver accepts; nothing was sent.</summary>
    TooLarge,

    /// <summary>The running copy answered that it will not act on the launch - it is closing, or found it malformed.</summary>
    Refused,

    /// <summary>Nothing accepted the connection in time, the write failed, or no acknowledgement came back.</summary>
    Unanswered
}

/// <summary>SP-0118: a later copy's end of the pipe.</summary>
public static class ActivationPipeClient
{
    /// <summary>The contract's bounded connect timeout (rule 3 allows 500-1000 ms).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>SP-0170: how long the sender waits for the acknowledgement once its line is written.</summary>
    public static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Delivers <paramref name="arguments"/> to the copy listening on <paramref name="pipeName"/> and waits for
    /// its acknowledgement. Never throws for a pipe fault; a launch the receiver's limits would refuse is not
    /// sent (<see cref="ActivationSendResult.TooLarge"/>).
    /// </summary>
    public static ActivationSendResult TrySend(string pipeName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        if (!ActivationMessage.TrySerialize(arguments, out var payload))
        {
            return ActivationSendResult.TooLarge;
        }

        // Off the caller's context: a UI caller blocks here, and nothing below may need it back.
        return Task.Run(() => SendAsync(pipeName, payload, timeout)).GetAwaiter().GetResult();
    }

    private static async Task<ActivationSendResult> SendAsync(string pipeName, byte[] payload, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);

            // One deadline for the write as well as the wait: the pipe is unbuffered, so a write to a receiver
            // that has stopped reading would otherwise never return.
            using var deadline = new CancellationTokenSource(AcknowledgementTimeout);
            await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);
            var reply = await ReadReplyAsync(client, deadline.Token).ConfigureAwait(false);
            return reply switch
            {
                ActivationPipeListener.AcceptedReply => ActivationSendResult.Delivered,
                ActivationPipeListener.RefusedReply => ActivationSendResult.Refused,
                _ => ActivationSendResult.Unanswered
            };
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return ActivationSendResult.Unanswered;
        }
    }

    /// <summary>The acknowledgement line, or <see langword="null"/> when the receiver hung up without one.</summary>
    private static async Task<string?> ReadReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new MemoryStream();
        var buffer = new byte[64];
        while (line.Length < 64)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            line.Write(buffer, 0, newline < 0 ? read : newline);
            if (newline >= 0)
            {
                return Encoding.ASCII.GetString(line.ToArray()).Trim();
            }
        }

        return null;
    }
}
