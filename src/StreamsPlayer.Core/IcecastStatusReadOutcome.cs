namespace StreamsPlayer.Core;

/// <summary>
/// Why an Icecast status metadata attempt ended. Whether titles were reported before it did lives on
/// <see cref="IcecastStatusReadResult.TitlesReported"/> (SP-0172): one flag on the result, not a
/// TitlesReported twin of every value here, so a new way to fail cannot forget the fact.
/// </summary>
public enum IcecastStatusReadOutcome
{
    /// <summary>Playback stopped or switched and the poll loop was torn down.</summary>
    Cancelled,

    /// <summary>
    /// The endpoint did not answer as an endpoint: a non-2xx answer, a network failure, or a request
    /// that outlived its deadline.
    /// </summary>
    EndpointUnavailable,

    /// <summary>The endpoint answered with something that is not an Icecast status document.</summary>
    Malformed,

    /// <summary>
    /// A readable status document that lists other mounts than the one being played - the endpoint
    /// works, it just cannot describe this channel (SP-0172, split from <see cref="Malformed"/>).
    /// </summary>
    NoMatchingMount
}
