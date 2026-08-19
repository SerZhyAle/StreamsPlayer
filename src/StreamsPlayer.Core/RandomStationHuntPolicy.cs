namespace StreamsPlayer.Core;

/// <summary>
/// SP-0086: how long a station that has never spoken is given to speak, and how many such stations the
/// random command tries before it stops and says so.
/// </summary>
/// <remarks>
/// Deliberately not shared with <see cref="LivePlaybackRecoveryPolicy"/>. That policy measures the gap
/// between reconnects of a station that already worked once; this measures the patience extended to a
/// station that has produced nothing at all. They are different quantities, and one constant serving both
/// would tie a change in reconnect behaviour to a change in how the random command feels.
/// Nothing validates in the constructor: this sits on a playback path, where throwing would trade a loud
/// test failure for a dead command.
/// </remarks>
public readonly record struct RandomStationHuntPolicy(int MaxAttempts, TimeSpan ConnectTimeout)
{
    /// <summary>The shipped values (owner's decision, 2026-08-10).</summary>
    public static RandomStationHuntPolicy Default { get; } = new(5, TimeSpan.FromSeconds(10));
}
