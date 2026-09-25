using System.Security.Cryptography;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0118: the names that make one running copy per session - the session-local lock the first copy
/// holds and the local pipe a later copy forwards its launch request over (<c>APP-ACTIVATION</c> rules
/// 2 and 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Frozen for the product's life.</b> Once shipped, a changed name lets an old copy and a new copy
/// run side by side and write the same state - the exact failure this exists to prevent. The two base
/// constants are the contract's own shapes for a new product and must never be edited.
/// </para>
/// <para>
/// The pipe name carries the Windows session id. The lock lives in the session's <c>Local\</c>
/// namespace already, but pipe names are machine-wide, so without the qualifier a second logon of the
/// same user would reach the other session's copy. A relocated data directory - the isolated profile
/// SP-0133's smoke gate runs against - gets a profile qualifier on both names, so it is an instance of
/// its own and never hands its launch to the owner's copy. The default data directory gets none: an
/// ordinary installation is strictly per session, as the contract prescribes. Both qualifiers are
/// recorded against the contract as a dated exception.
/// </para>
/// </remarks>
public sealed record SingleInstanceIdentity(string MutexName, string PipeName)
{
    /// <summary>The contract's lock name for this product (rule 2). Frozen.</summary>
    public const string ProductMutexName = @"Local\StreamsPlayerSingleInstance";

    /// <summary>The contract's pipe name for this product (rule 3), before the session qualifier. Frozen.</summary>
    public const string ProductPipeName = "sza-streamsplayer-single-instance";

    /// <summary>
    /// The identity for a copy that keeps its state in <paramref name="dataDirectory"/> and runs in
    /// Windows session <paramref name="sessionId"/>.
    /// </summary>
    public static SingleInstanceIdentity For(string dataDirectory, string defaultDataDirectory, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDataDirectory);

        var profile = Normalize(dataDirectory);
        var profileSuffix = profile == Normalize(defaultDataDirectory)
            ? string.Empty
            : "-p" + ProfileHash(profile);
        return new(
            ProductMutexName + profileSuffix,
            $"{ProductPipeName}-s{sessionId}{profileSuffix}");
    }

    private static string Normalize(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();

    private static string ProfileHash(string normalizedDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDirectory)))[..12].ToLowerInvariant();
}
