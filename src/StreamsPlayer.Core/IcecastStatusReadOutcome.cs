namespace StreamsPlayer.Core;

/// <summary>How an Icecast status metadata attempt ended.</summary>
public enum IcecastStatusReadOutcome
{
    TitlesReported,
    Cancelled,
    EndpointUnavailable,
    Malformed
}
