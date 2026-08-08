namespace StreamsPlayer.Core;

/// <summary>
/// SP-0026 - the FFmpeg native libraries the opt-in FlyleafLib video engine binds against, and where
/// they live. Declaring the set once keeps the installer, the "is it installed" probe and the removal
/// path from drifting apart.
/// </summary>
/// <remarks>
/// These are deliberately not shipped in any package: the natives published alongside FlyleafLib are
/// built <c>--enable-gpl --enable-version3</c>, so bundling them would place the whole application
/// under GPLv3. They are fetched on an explicit user request instead - see
/// <see cref="FFmpegComponentsInstaller"/>, which pulls an LGPL-3.0 build. LibVLC remains the default
/// engine and needs none of this.
/// </remarks>
public static class FFmpegComponents
{
    /// <summary>The folder name used both beside the executable and under the per-user data directory.</summary>
    public const string FolderName = "FFmpeg";

    /// <summary>
    /// The libraries FlyleafLib's engine loads, named with the ABI suffixes it resolves. Both the
    /// upstream Flyleaf asset and the LGPL build in <see cref="FFmpegComponentsInstaller"/> export
    /// exactly these, which is what lets <c>Flyleaf.FFmpeg.Bindings</c> bind against either.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredLibraries =
    [
        "avcodec-62.dll",
        "avdevice-62.dll",
        "avfilter-11.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "swresample-6.dll",
        "swscale-9.dll"
    ];

    /// <summary>The components folder inside a data directory, for example <c>%LOCALAPPDATA%\StreamsPlayer</c>.</summary>
    public static string ResolveFolder(string dataDirectory) =>
        Path.Combine(dataDirectory, FolderName);

    /// <summary>
    /// The required libraries absent from <paramref name="folder"/>, in declaration order. A
    /// zero-length file counts as missing: an interrupted copy leaves one behind, and reporting it as
    /// present would send the engine into a load failure instead of an actionable "not installed".
    /// </summary>
    public static IReadOnlyList<string> MissingLibraries(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return RequiredLibraries;
        }

        var missing = new List<string>();
        foreach (var library in RequiredLibraries)
        {
            var file = new FileInfo(Path.Combine(folder, library));
            if (!file.Exists || file.Length == 0)
            {
                missing.Add(library);
            }
        }

        return missing;
    }

    /// <summary>Whether every required library is present in <paramref name="folder"/>.</summary>
    public static bool IsInstalled(string folder) => MissingLibraries(folder).Count == 0;

    /// <summary>
    /// Deletes the required libraries from <paramref name="folder"/> and then the folder itself, but
    /// only when nothing else is left in it. A user who hand-deployed the natives may keep other files
    /// (a licence copy, the extra Flyleaf plugins) in the same place, and an uninstall of our
    /// components is not a licence to remove those.
    /// </summary>
    public static void Remove(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var library in RequiredLibraries)
        {
            var path = Path.Combine(folder, library);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }
}
