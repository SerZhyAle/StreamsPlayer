using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0109: what a failed, user-requested operation can honestly tell the user about why it failed.
/// </summary>
/// <remarks>
/// <c>APP-BEHAVIOUR</c> rule 6 sends the exception to the log and gives the user a named cause and an
/// action. The exception's own text cannot be that cause: it is in the runtime's language rather than the
/// user's, it names the engine's internals, and it changes wording between runtime versions. Four causes
/// are all a user can act on differently - retry after the network, retry later or report, free the disk,
/// or report - so four is the whole vocabulary.
/// </remarks>
public enum FailureCause
{
    Unknown,
    Network,
    DamagedData,
    Storage
}

/// <summary>SP-0109: maps an exception to the <see cref="FailureCause"/> the user is told.</summary>
public static class FailureCauseClassifier
{
    /// <summary>
    /// Walks the exception and its inner exceptions from the outside in, returning the first network or
    /// data cause found; only when neither appears anywhere in the chain is a file-system type in it read
    /// as storage.
    /// </summary>
    /// <remarks>
    /// The order is the point. <see cref="HttpIOException"/> derives from <see cref="IOException"/>, and a
    /// dropped connection often surfaces as an <see cref="IOException"/> wrapping a
    /// <see cref="SocketException"/>, so checking the file-system types first would tell a user with a
    /// dead network to go and free disk space. <see cref="TaskCanceledException"/> is the shape an
    /// <see cref="HttpClient"/> timeout takes; a cancellation the user asked for is handled by every
    /// caller before it reaches a failure path, which is why a bare <see cref="OperationCanceledException"/>
    /// stays <see cref="FailureCause.Unknown"/>.
    /// </remarks>
    public static FailureCause Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException or HttpIOException or SocketException or TimeoutException
                    or TaskCanceledException:
                    return FailureCause.Network;
                case InvalidDataException or JsonException:
                    return FailureCause.DamagedData;
            }
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException)
            {
                return FailureCause.Storage;
            }
        }

        return FailureCause.Unknown;
    }
}
