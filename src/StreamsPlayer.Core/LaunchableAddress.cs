using System.Diagnostics.CodeAnalysis;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0124: the one reading of a channel address for launch. <c>STREAM-BANK</c> defines the launchable
/// schemes as exactly <c>http</c>, <c>https</c> and <c>rtsp</c>; anything else - <c>file://</c>, a network
/// share, a relative path, an address that does not parse - is never handed to a playback, capture or
/// probe engine, whatever path the row arrived by.
/// </summary>
/// <remarks>
/// A bank or playlist row with another scheme is still stored (the contract's reference importer does the
/// same); it is refused here, at launch, instead. The share case is the reason this is a gate and not a
/// courtesy: a <c>\\server\share</c> address opened by a media engine on Windows can send the user's
/// credentials to that server.
/// </remarks>
public static class LaunchableAddress
{
    private const string RtspScheme = "rtsp";

    /// <summary>The address as an absolute <see cref="Uri"/>, when it is one of the three launchable schemes.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? address)
    {
        // Uri.Scheme is always lower case, so an ordinal comparison covers every letter case of the input.
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Host.Length > 0 &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == RtspScheme))
        {
            address = parsed;
            return true;
        }

        address = null;
        return false;
    }

    /// <summary>
    /// A launchable address that is also <c>http</c> or <c>https</c> - what an HTTP-only probe may open.
    /// </summary>
    public static bool TryParseHttp(string? value, [NotNullWhen(true)] out Uri? address)
    {
        if (TryParse(value, out var parsed) && parsed.Scheme != RtspScheme)
        {
            address = parsed;
            return true;
        }

        address = null;
        return false;
    }

    public static bool IsLaunchable(string? value) => TryParse(value, out _);

    /// <summary>
    /// The host a new row is titled by when no title was given; the address itself when it has no
    /// launchable host, so a title is always produced and nothing here can throw.
    /// </summary>
    public static string HostOf(string value) => TryParse(value, out var address) ? address.Host : value.Trim();
}
