namespace StreamsPlayer.Core;

/// <summary>
/// DEVICE-EXCHANGE item AO: an account is of one kind. A <c>member</c> account's devices publish, broadcast and
/// cast; a <c>guest</c> is given things and offers nothing - the server refuses its devices every producer
/// operation with <c>not-permitted</c>, so a client hides those actions instead of offering them to be refused.
/// </summary>
public enum ExchangeAccountKind
{
    Member,
    Guest
}

public static class ExchangeAccountKinds
{
    /// <summary>
    /// The kind a <c>welcome</c> or <c>enrolled</c> frame names. An absent member is a 0.20 server, whose every
    /// account is a member (item AO). A value this build does not know is read as <see cref="ExchangeAccountKind.Guest"/>:
    /// the restrictive reading hides producer actions, where a permissive one would offer them to be refused.
    /// </summary>
    public static ExchangeAccountKind Parse(string? value) => value switch
    {
        null or "member" => ExchangeAccountKind.Member,
        _ => ExchangeAccountKind.Guest
    };
}

/// <summary>Bounds this product puts on how many account entries one PC holds.</summary>
public static class ExchangeEntryLimits
{
    /// <summary>
    /// The guest entries held beside the one own entry (SP-0221 requirement 9). The contract sets no number; each
    /// entry is a stored token and, when its list is opened, one control stream, so the bound keeps both small.
    /// Settings states it.
    /// </summary>
    public const int MaximumGuestEntries = 5;
}
