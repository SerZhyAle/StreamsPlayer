using System.IO.Compression;

namespace StreamsPlayer.Core;

/// <summary>Bytes received so far and, when the server declared one, the total to expect.</summary>
public readonly record struct FFmpegInstallProgress(long ReceivedBytes, long? TotalBytes)
{
    /// <summary>Completion in the range 0..1, or <c>null</c> when the total length is unknown.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// SP-0026 downloader for the FFmpeg native libraries the opt-in FlyleafLib engine needs. Called only
/// from an explicitly accepted user offer - there is no automatic, startup or background fetch, the
/// same rule the stream catalog and the channel-preview atlas follow.
/// </summary>
public sealed class FFmpegComponentsInstaller
{
    /// <summary>
    /// An <b>LGPL-3.0</b> shared build. This must never be swapped for a <c>-gpl-</c> asset: the
    /// natives published alongside FlyleafLib itself are built <c>--enable-gpl --enable-version3</c>,
    /// and pulling those would put the user's installation under GPLv3 terms the product does not
    /// carry. The <c>lgpl-shared</c> variant reports <c>LGPL version 3 or later</c> and exports the
    /// same sonames, so <c>Flyleaf.FFmpeg.Bindings</c> binds against it unchanged.
    /// </summary>
    /// <remarks>
    /// The <c>latest</c> tag is republished continuously, so the bytes behind this URL move. The
    /// <c>n8.1</c> in the asset name is what actually matters - it pins the ABI generation the
    /// bindings were generated for. A version-pinned tag would go stale and 404 instead.
    /// </remarks>
    public const string SourceUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";

    /// <summary>Human-readable source, shown in the confirmation so the user can see what is fetched.</summary>
    public const string SourceDescription = "BtbN/FFmpeg-Builds - FFmpeg n8.1 win64 shared (LGPL-3.0)";

    /// <summary>The archive is ~67 MB today; this refuses a mispublished or redirected asset.</summary>
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;

    /// <summary>Approximate download size, for the confirmation copy. Not a gate.</summary>
    public const int ApproximateDownloadMegabytes = 67;

    /// <summary>Approximate installed size, for the confirmation copy. Not a gate.</summary>
    public const int ApproximateInstalledMegabytes = 143;

    // Sized for a slow link on a payload an order of magnitude larger than the preview atlas.
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;

    public FFmpegComponentsInstaller(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Downloads the archive and extracts the required libraries into
    /// <see cref="FFmpegComponents.ResolveFolder"/> for <paramref name="dataDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Staged through temporary paths and moved into place only once every required library has been
    /// extracted, so a failure or a cancellation never leaves a partial set that
    /// <see cref="FFmpegComponents.IsInstalled"/> would accept and the engine would then fail to load.
    /// </remarks>
    /// <returns>The folder the components were installed into.</returns>
    public async Task<string> InstallAsync(
        string dataDirectory,
        IProgress<FFmpegInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        var target = FFmpegComponents.ResolveFolder(dataDirectory);
        var staging = $"{target}.incoming-{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"streamsplayer-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadArchiveAsync(archivePath, progress, deadline.Token);
            ExtractRequiredLibraries(archivePath, staging);
            PublishStaging(staging, target);
            return target;
        }
        finally
        {
            Discard(archivePath);
            DiscardFolder(staging);
        }
    }

    private async Task DownloadArchiveAsync(
        string archivePath,
        IProgress<FFmpegInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength;
        if (declared > MaximumArchiveBytes)
        {
            throw new InvalidDataException(
                $"The FFmpeg archive is larger than the {MaximumArchiveBytes} byte ceiling.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long received = 0;
        progress?.Report(new FFmpegInstallProgress(0, declared));

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            received += read;
            // A server may under-declare or omit Content-Length, so the ceiling is enforced on the
            // bytes actually arriving, not only on the header.
            if (received > MaximumArchiveBytes)
            {
                throw new InvalidDataException(
                    $"The FFmpeg archive is larger than the {MaximumArchiveBytes} byte ceiling.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new FFmpegInstallProgress(received, declared));
        }
    }

    private static void ExtractRequiredLibraries(string archivePath, string staging)
    {
        Directory.CreateDirectory(staging);
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var required in FFmpegComponents.RequiredLibraries)
        {
            // Matched on the file name alone: the build nests everything under a versioned folder whose
            // name carries the build date, and the bundled ffmpeg/ffplay/ffprobe executables are simply
            // never asked for, which is most of the archive left undisturbed.
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, required, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException($"The FFmpeg archive does not contain {required}.");
            }

            entry.ExtractToFile(Path.Combine(staging, required), overwrite: true);
        }
    }

    private static void PublishStaging(string staging, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Move each library rather than the folder: the target may already hold an older or partial set,
        // and a directory move onto an existing directory fails.
        Directory.CreateDirectory(target);
        foreach (var library in FFmpegComponents.RequiredLibraries)
        {
            File.Move(Path.Combine(staging, library), Path.Combine(target, library), overwrite: true);
        }
    }

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing an otherwise complete install over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DiscardFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
