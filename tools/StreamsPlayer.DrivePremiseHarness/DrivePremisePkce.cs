using System.Security.Cryptography;

namespace StreamsPlayer.DrivePremiseHarness;

/// <summary>
/// The PKCE pair of SP-0202's premise experiment. The sign-in the ticket requires for the product
/// (requirement 2: an installed-app flow with PKCE and a loopback redirect) is exercised here first,
/// so the experiment also proves the flow the consumer work would ship - not only the folder premise.
/// </summary>
public static class DrivePremisePkce
{
    /// <summary>RFC 7636 unreserved characters; 64 code points is inside the 43-128 the RFC allows.</summary>
    private const string VerifierAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    public const int VerifierLength = 64;

    public static string NewVerifier()
    {
        return RandomNumberGenerator.GetString(VerifierAlphabet, VerifierLength);
    }

    public static string Challenge(string verifier)
    {
        var digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The <c>state</c> the redirect must echo back, so a stale browser tab cannot answer for this run.</summary>
    public static string NewState()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }
}
