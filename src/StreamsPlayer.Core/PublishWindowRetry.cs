using System.Net;
using System.Net.Http;

namespace StreamsPlayer.Core;

/// <summary>What the publish window looked like when a fetch hit it.</summary>
public enum PublishWindowCause
{
    /// <summary>The asset answered 404: it has been deleted and not yet re-uploaded.</summary>
    NotFound,

    /// <summary>The body ended before the length the server declared for it.</summary>
    ShortRead,

    /// <summary>The body arrived whole by HTTP's account but is not a readable ZIP.</summary>
    TruncatedArchive
}

/// <summary>One scheduled retry: which attempt comes next, out of how many, and after how long.</summary>
public readonly record struct PublishWindowRetryNotice(
    int NextAttempt,
    int MaximumAttempts,
    TimeSpan Delay,
    PublishWindowCause Cause);

/// <summary>
/// A downloaded ZIP that cannot be opened as an archive at all - the shape a body cut off mid-publish
/// takes when the transport did not notice. <see cref="InvalidDataException"/> is sealed, so the archive
/// reader's own <see cref="InvalidDataException"/> travels as the inner exception: the failure-cause
/// classifier walks the chain and still tells the user the data was damaged once the retries are spent.
/// </summary>
public sealed class TruncatedArchiveException(string message, InvalidDataException innerException)
    : Exception(message, innerException);

/// <summary>
/// SP-0107, <c>STREAM-BANK</c> rule 11 (amendment item H): publishing replaces a release asset by
/// delete-then-upload, so for a few seconds on every publish the asset answers 404, or a transfer that
/// began just before the delete ends short. The contract names that an expected state and asks for a
/// retry rather than an error.
/// </summary>
/// <remarks>
/// <para>The retry lives entirely inside one user-started operation and takes that operation's
/// cancellation token, so it never turns the explicit refresh (rule 1) into a background download:
/// cancelling the operation cancels the wait, and when the schedule is spent the last failure surfaces
/// exactly as it did before this class existed.</para>
/// <para>Only the three outcomes the contract calls expected are retried. A body over its ceiling, a
/// CSV that does not parse, a manifest mismatch, a timeout, a DNS failure - each is still an error on the
/// first attempt, because none of them is what a publish in progress looks like and repeating them would
/// only make the user wait longer for the same message.</para>
/// </remarks>
public sealed class PublishWindowRetry
{
    /// <summary>
    /// The shipped schedule: three retries after 3, 6 and 12 seconds, so at most 21 seconds of waiting.
    /// The window is a delete followed by one asset upload, which is seconds rather than minutes; a
    /// schedule long enough to outlast a genuinely missing asset would only delay the honest error.
    /// </summary>
    public static PublishWindowRetry Default { get; } = new(
        [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12)]);

    private readonly IReadOnlyList<TimeSpan> _delays;

    /// <param name="delays">The pause before each retry; its length is the retry count.</param>
    public PublishWindowRetry(IReadOnlyList<TimeSpan> delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        _delays = delays;
    }

    /// <summary>The pause before each retry.</summary>
    public IReadOnlyList<TimeSpan> Delays => _delays;

    /// <summary>Total attempts, the first one included.</summary>
    public int MaximumAttempts => _delays.Count + 1;

    /// <summary>
    /// Runs <paramref name="fetch"/>, repeating it while it fails in a way <see cref="Classify"/>
    /// recognises as the publish window and the schedule still has a delay left.
    /// </summary>
    /// <param name="fetch">
    /// The whole fetch, from the first request to the parsed result. It is re-run from the start, never
    /// resumed: after a publish every file may belong to the new build, so a partial result from the old
    /// one must not be completed with pieces of the new.
    /// </param>
    /// <param name="retrying">Told before each pause, so the user sees a retry rather than silence.</param>
    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> fetch,
        IProgress<PublishWindowRetryNotice>? retrying,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await fetch(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt <= _delays.Count
                && !cancellationToken.IsCancellationRequested
                && Classify(exception) is not null)
            {
                var delay = _delays[attempt - 1];
                retrying?.Report(new PublishWindowRetryNotice(
                    attempt + 1, MaximumAttempts, delay, Classify(exception)!.Value));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Which publish-window outcome <paramref name="exception"/> is, or <c>null</c> when it is any other
    /// failure. Inner exceptions are searched because the HTTP stack reports a cut-off body wrapped in
    /// whatever the reading layer adds around it.
    /// </summary>
    public static PublishWindowCause? Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.NotFound }:
                    return PublishWindowCause.NotFound;
                case HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded }:
                    return PublishWindowCause.ShortRead;
                case TruncatedArchiveException:
                    return PublishWindowCause.TruncatedArchive;
            }
        }

        return null;
    }
}
