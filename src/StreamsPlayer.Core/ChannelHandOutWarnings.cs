namespace StreamsPlayer.Core;

/// <summary>
/// SP-0058 / SP-0201 requirement 5: which confirmations a channel address must pass before it is put
/// anywhere another person or a bug report can read it.
/// </summary>
/// <remarks>
/// One rule for every hand-out of a raw address - the share text, and the About window's "Copy all" -
/// so a new surface cannot forget half of it. The result is the localization keys of the warnings, in
/// the order they are asked; the caller owns the dialogs and treats a refusal of any as a refusal of the
/// whole hand-out. An address that carries neither a credential nor a listening capability needs none.
/// </remarks>
public static class ChannelHandOutWarnings
{
    public const string CredentialKey = "ShareCredentialWarning";
    public const string CapabilityKey = "BroadcastCapabilityWarning";

    public static IReadOnlyList<string> KeysFor(string url)
    {
        var keys = new List<string>(2);
        if (CatalogUrlIdentity.HasCredentials(url))
        {
            keys.Add(CredentialKey);
        }

        if (BroadcastCapabilityAddress.CarriesCapability(url))
        {
            keys.Add(CapabilityKey);
        }

        return keys;
    }
}
