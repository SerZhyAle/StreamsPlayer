using System.IO.Pipes;

namespace StreamsPlayer.Core;

/// <summary>How a forwarded command ended, from the second process's side (SP-0271).</summary>
public enum AppCommandDelivery
{
    /// <summary>A response line of this profile came back.</summary>
    Answered,

    /// <summary>
    /// A line came back that is not a JSON object with <c>schemaVersion</c> - an older first instance's plain
    /// <c>ok</c> or <c>refused</c> (the research proposal of 2026-11-14, item 4).
    /// </summary>
    LegacyReply,

    /// <summary>Nothing accepted the connection within the delivery bound.</summary>
    NotConnected,

    /// <summary>
    /// The connection was taken but no answer came within the completion bound. Silence is not evidence of an older copy
    /// (item K paragraph 3): a first instance that is busy with a slow verb answers later.
    /// </summary>
    NoAnswer,

    /// <summary>
    /// SP-0279, item K: the connection was taken and then closed or broken with no response line - positive evidence that the
    /// running copy is older than the profile or is a build that never takes commands.
    /// </summary>
    Closed,

    /// <summary>
    /// SP-0279, item I.3: a response line came back whose <c>requestId</c> is empty or is not this request's - an answer to
    /// something else, or the answer to a request that could not be read. It proves nothing about this command.
    /// </summary>
    Unreadable
}

/// <summary>The outcome of <see cref="AppCommandClient.Send"/>.</summary>
public sealed record AppCommandExchange(AppCommandDelivery Delivery, AppCommandResponse? Response = null, string? LegacyReply = null);

/// <summary>
/// SP-0271: the second process's end of the <c>APP-COMMANDS</c> rule 7 pipe profile - one request line, one response
/// line, on the frozen pipe of <see cref="SingleInstanceIdentity"/>. Never throws for a pipe fault.
/// </summary>
public static class AppCommandClient
{
    /// <summary>The delivery bound of rule 8 - the connect bound of <c>APP-ACTIVATION</c> rule 3.</summary>
    public static readonly TimeSpan DeliveryBound = ActivationPipeClient.ConnectTimeout;

    public static AppCommandExchange Send(string pipeName, AppCommandRequest request, TimeSpan connectTimeout, TimeSpan completion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(request);
        var payload = request.Serialize();
        var requestId = request.RequestId;

        // Off the caller's context, as the launch client does: nothing below may need it back.
        return Task.Run(() => SendAsync(pipeName, payload, requestId, connectTimeout, completion)).GetAwaiter().GetResult();
    }

    private static async Task<AppCommandExchange> SendAsync(
        string pipeName, byte[] payload, string requestId, TimeSpan connectTimeout, TimeSpan completion)
    {
        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync((int)connectTimeout.TotalMilliseconds).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return new AppCommandExchange(AppCommandDelivery.NotConnected);
        }

        try
        {
            using var deadline = new CancellationTokenSource(completion);
            await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);
            var read = await ReadLineAsync(client, deadline.Token).ConfigureAwait(false);
            if (read.Line is not { } line)
            {
                // A hang-up with no line is evidence of an older copy (item K); a line over the bound proves nothing.
                return new AppCommandExchange(read.Closed ? AppCommandDelivery.Closed : AppCommandDelivery.Unreadable);
            }

            if (!AppCommandResponse.TryParse(line, out var response))
            {
                return new AppCommandExchange(AppCommandDelivery.LegacyReply, LegacyReply: line.Trim());
            }

            // Item I.3: an empty or a foreign request id is an answer to something else - or to a line the first instance
            // could not read. It is never taken for this command's answer.
            return string.Equals(response.RequestId, requestId, StringComparison.Ordinal)
                ? new AppCommandExchange(AppCommandDelivery.Answered, response)
                : new AppCommandExchange(AppCommandDelivery.Unreadable, response);
        }
        catch (IOException)
        {
            // The pipe broke after the line was taken: the receiver is gone, which is the same evidence as a hang-up.
            return new AppCommandExchange(AppCommandDelivery.Closed);
        }
        catch (Exception exception) when (exception is TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new AppCommandExchange(AppCommandDelivery.NoAnswer);
        }
    }

    /// <summary>
    /// A first line as <see cref="ReadLineAsync"/> read it. <see cref="Line"/> is <see langword="null"/> when the receiver hung
    /// up without one (<see cref="Closed"/>) or the line is over the bound of item L.
    /// </summary>
    private readonly record struct LineRead(string? Line, bool Closed);

    private static async Task<LineRead> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new MemoryStream();
        var buffer = new byte[4096];
        while (line.Length <= AppCommandLimits.MaximumResponseBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Length == 0
                    ? new LineRead(null, true)
                    : new LineRead(AppCommandText.Utf8.GetString(line.ToArray()), false);
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            line.Write(buffer, 0, newline < 0 ? read : newline);
            if (newline >= 0)
            {
                return new LineRead(AppCommandText.Utf8.GetString(line.ToArray()), false);
            }
        }

        return new LineRead(null, false);
    }
}
