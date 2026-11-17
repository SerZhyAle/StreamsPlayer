using System.Reflection;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0275: the version an executable was stamped with (<c>YY.MMDD.HHmm</c>), read from its informational-version attribute
/// the way the player has always read it. Both executables of a package are built from one command line and one stamp, so
/// the console companion and the player report the same value.
/// </summary>
public static class BuildVersion
{
    /// <summary>The stamp of <paramref name="assembly"/> without the source-revision suffix, or <c>Unknown</c>.</summary>
    public static string Of(Assembly? assembly) =>
        (assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown").Split('+')[0];
}
