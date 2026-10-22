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

    /// <summary>
    /// SP-0212 (L28): the export gate's own credential wording - a file the user places somewhere is a
    /// different hand-out from a chat message, and its message says so. The condition stays the one
    /// rule's, so a credential-bearing row cannot slip past the wording it was written for.
    /// </summary>
    public const string ExportCredentialKey = "ExportCredentialWarning";

    /// <summary>
    /// SP-0212 (L28): the keys a hand-out of many addresses at once owes. The capability confirmation is
    /// the owner's own key, so a warning added to <see cref="KeysFor"/> reaches every hand-out - the
    /// share menu, the export gate and the About window's Copy all - not only the ones written first.
    /// </summary>
    public static IReadOnlyList<string> ExportKeysFor(IEnumerable<string> urls)
    {
        var keys = new List<string>(2);
        if (urls.Any(CatalogUrlIdentity.HasCredentials))
        {
            keys.Add(ExportCredentialKey);
        }

        if (urls.Any(BroadcastCapabilityAddress.CarriesCapability))
        {
            keys.Add(CapabilityKey);
        }

        return keys;
    }

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

    /// <summary>
    /// 196 section 6.4: the export gate for whole ROWS. A broadcast-resource row carries its token in
    /// the URL path, which <see cref="CatalogUrlIdentity.HasCredentials"/> does not see - there is no
    /// <c>user:pass@</c> - so a row with a <see cref="StreamChannel.ResourceKey"/> is a secret-bearing
    /// row and gets the SAME confirmation a credential-bearing address gets, before the exported M3U
    /// hands a stranger the token. Publishing broadcast rows into the shared stream catalog stays
    /// forbidden outright (they are <see cref="SourceOrigin.Imported"/> user rows and never reach it).
    /// </summary>
    public static IReadOnlyList<string> ExportKeysForRows(IEnumerable<StreamChannel> rows)
    {
        var keys = new List<string>(2);
        var rowList = rows as IList<StreamChannel> ?? rows.ToList();
        if (rowList.Any(row => CatalogUrlIdentity.HasCredentials(row.Url)) ||
            rowList.Any(row => row.ResourceKey is { Length: > 0 }))
        {
            keys.Add(ExportCredentialKey);
        }

        if (rowList.Any(row => BroadcastCapabilityAddress.CarriesCapability(row.Url)))
        {
            keys.Add(CapabilityKey);
        }

        return keys;
    }
}
