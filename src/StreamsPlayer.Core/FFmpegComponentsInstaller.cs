using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace StreamsPlayer.Core;

/// <summary>Bytes received so far and, when the server declared one, the total to expect.</summary>
public readonly record struct FFmpegInstallProgress(long ReceivedBytes, long? TotalBytes)
{
    /// <summary>Completion in the range 0..1, or <c>null</c> when the total length is unknown.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// SP-0128: exactly which archive the installer accepts - an address that never moves, and the length and
/// SHA-256 digest of the bytes behind it. The digest is pinned in the application (SP-0128 open question 1,
/// option a): the trust chain is the signed app release itself, and a new FFmpeg build needs a new app
/// release, which is acceptable for a set that changes rarely.
/// </summary>
public sealed record FFmpegComponentsSource(string Url, long Length, string Sha256);

/// <summary>
/// The downloaded archive is not the pinned build - its length or its SHA-256 digest differs. Nothing
/// was extracted. A distinct type so the message can say "not the expected file" rather than "damaged".
/// </summary>
public sealed class FFmpegArchiveMismatchException(string message) : Exception(message);

/// <summary>
/// The installed set could not be replaced as a whole, and restoring the previous set failed as well.
/// The previous libraries were kept in <see cref="PreservedFolder"/> rather than deleted.
/// </summary>
public sealed class FFmpegComponentsRollbackException(string message, string preservedFolder, Exception inner)
    : IOException(message, inner)
{
    public string PreservedFolder { get; } = preservedFolder;
}

/// <summary>
/// SP-0026 downloader for the FFmpeg native libraries the opt-in FlyleafLib engine needs. Called only
/// from an explicitly accepted user offer - there is no automatic, startup or background fetch, the
/// same rule the stream catalog and the channel-preview atlas follow.
/// </summary>
/// <remarks>
/// SP-0128: the archive is native code that will run inside the process, so it is fetched from a fixed
/// build, verified against a pinned length and digest before anything is extracted, bounded by
/// inactivity rather than by total duration, cancellable, and published as a whole set or not at all.
/// </remarks>
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
    /// A dated <c>autobuild-*</c> tag, never <c>latest</c>: <c>latest</c> is republished daily, so no
    /// digest could be pinned against it. BtbN prunes its daily tags after a few weeks but keeps the
    /// month-end ones (they reach back to 2024-10 at the time of writing), which is why the pin is a
    /// month-end build. The <c>n8.1</c> ABI generation must match the bindings. To move the pin: pick a
    /// newer month-end tag, download its <c>win64-lgpl-shared-8.1</c> asset, and update all three values
    /// from the file itself - its byte length and <c>Get-FileHash -Algorithm SHA256</c>.
    /// </remarks>
    public static readonly FFmpegComponentsSource PinnedSource = new(
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-31-13-27/ffmpeg-n8.1.2-50-g1a748fe2cd-win64-lgpl-shared-8.1.zip",
        70_835_150,
        "e9712ffbdb03ef71bbab660c75b835bfe698ef6fad0247c76d8d394a39a3db63");

    /// <summary>The pinned address, kept as a constant for the licence guard and the log.</summary>
    public static string SourceUrl => PinnedSource.Url;

    /// <summary>Human-readable source, shown in the confirmation so the user can see what is fetched.</summary>
    public const string SourceDescription = "BtbN/FFmpeg-Builds - FFmpeg n8.1.2 win64 shared (LGPL-3.0), build 2026-08-31";

    /// <summary>Download size in MiB, for the confirmation copy. Not a gate - the pinned length is.</summary>
    public const int ApproximateDownloadMegabytes = 68;

    /// <summary>Installed size of the seven libraries in MiB, for the confirmation copy. Not a gate.</summary>
    public const int ApproximateInstalledMegabytes = 137;

    /// <summary>
    /// How long the transfer may deliver nothing - before the headers arrive or between two chunks of the
    /// body - before it is abandoned. A slow link that keeps sending finishes however long it takes.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    private const string StagingMarker = ".incoming-";
    private const string DownloadMarker = ".download-";
    private const string PreviousMarker = ".previous-";

    private readonly HttpClient _httpClient;
    private readonly FFmpegComponentsSource _source;
    private readonly TimeSpan _idleTimeout;

    /// <param name="httpClient">
    /// Its <c>HttpClient.Timeout</c> is irrelevant past the head - it never applies to a body read after
    /// the head has arrived (SP-0129, <c>HttpClientTimeoutPremiseTests</c>) - and this installer bounds the
    /// head and the body's silence itself.
    /// </param>
    /// <param name="source">The archive to accept; <see cref="PinnedSource"/> unless a test says otherwise.</param>
    /// <param name="idleTimeout">The silence bound; <see cref="DefaultIdleTimeout"/> unless a test says otherwise.</param>
    public FFmpegComponentsInstaller(
        HttpClient httpClient,
        FFmpegComponentsSource? source = null,
        TimeSpan? idleTimeout = null)
    {
        _httpClient = httpClient;
        _source = source ?? PinnedSource;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
    }

    /// <summary>
    /// Downloads and verifies the archive, then installs the required libraries into
    /// <see cref="FFmpegComponents.ResolveFolder"/> for <paramref name="dataDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Every intermediate file lives beside the target, inside <paramref name="dataDirectory"/>, and is
    /// removed on every exit path - success, failure or cancellation. The installed set changes only in
    /// the final step, which replaces it as a whole or restores the previous one.
    /// </remarks>
    /// <returns>The folder the components were installed into.</returns>
    /// <exception cref="FFmpegArchiveMismatchException">The archive is not the pinned one.</exception>
    /// <exception cref="InvalidDataException">The archive lacks a library, or overran the pinned length.</exception>
    /// <exception cref="TimeoutException">The transfer delivered nothing for the idle bound.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<string> InstallAsync(
        string dataDirectory,
        IProgress<FFmpegInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var target = FFmpegComponents.ResolveFolder(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        SweepLeftovers(dataDirectory, target);

        var suffix = Guid.NewGuid().ToString("N");
        var staging = $"{target}{StagingMarker}{suffix}";
        var archivePath = $"{target}{DownloadMarker}{suffix}.zip";

        try
        {
            await DownloadArchiveAsync(archivePath, progress, cancellationToken);
            await VerifyArchiveAsync(archivePath, cancellationToken);
            await ExtractRequiredLibrariesAsync(archivePath, staging, cancellationToken);
            // Past this point a cancel no longer applies: the swap is short and must not be interrupted.
            // Off the caller's thread: the caller resumed on the UI thread, and the swap touches the disk.
            await Task.Run(() => PublishStaging(staging, target), CancellationToken.None);
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
        // The silence bound also covers the wait for headers: a TLS or proxy stall before the response
        // is as much "nothing arriving" as a body that stops mid-way.
        HttpResponseMessage response;
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            headers.CancelAfter(_idleTimeout);
            try
            {
                response = await _httpClient.GetAsync(
                    _source.Url, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The server sent no response for {_idleTimeout.TotalSeconds:0} seconds.");
            }
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength;
            if (declared is not null && declared != _source.Length)
            {
                throw new FFmpegArchiveMismatchException(
                    $"The FFmpeg archive declares {declared} bytes; the pinned build is {_source.Length}.");
            }

            await using var destination = new FileStream(
                archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            var relay = progress is null
                ? null
                : new ProgressRelay(report => progress.Report(new FFmpegInstallProgress(report.ReceivedBytes, report.TotalBytes)));

            // The ceiling is the pinned length itself: one byte more is already not the pinned file.
            await HttpDownload.CopyToAsync(
                response, destination, relay, _source.Length, _idleTimeout, cancellationToken);
        }
    }

    private async Task VerifyArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        if (stream.Length != _source.Length)
        {
            throw new FFmpegArchiveMismatchException(
                $"The FFmpeg archive is {stream.Length} bytes; the pinned build is {_source.Length}.");
        }

        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(digest, _source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FFmpegArchiveMismatchException(
                $"The FFmpeg archive digest {digest} does not match the pinned {_source.Sha256}.");
        }
    }

    /// <summary>
    /// Extracts the required libraries one file at a time, off the caller's thread (SP-0165).
    /// </summary>
    /// <remarks>
    /// The caller resumes on the UI thread, where this loop used to run as one synchronous stretch of
    /// roughly 137 MB the window could not answer during. Cancellation is honoured between files, so a
    /// cancel never lands halfway through one and the previous set stays intact until the swap starts.
    /// <paramref name="beforeFile"/> exists for tests: it runs ahead of each file's cancellation check.
    /// </remarks>
    internal static async Task ExtractRequiredLibrariesAsync(
        string archivePath,
        string staging,
        CancellationToken cancellationToken,
        Func<string, Task>? beforeFile = null)
    {
        Directory.CreateDirectory(staging);
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var required in FFmpegComponents.RequiredLibraries)
        {
            if (beforeFile is not null)
            {
                await beforeFile(required);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Matched on the file name alone: the build nests everything under a versioned folder whose
            // name carries the build date, and the bundled ffmpeg/ffplay/ffprobe executables are simply
            // never asked for, which is most of the archive left undisturbed.
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, required, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException($"The FFmpeg archive does not contain {required}.");
            }

            await Task.Run(
                () => entry.ExtractToFile(Path.Combine(staging, required), overwrite: true),
                cancellationToken);
        }
    }

    /// <summary>
    /// Replaces the required libraries in <paramref name="target"/> as a set. The current ones are first
    /// moved aside into a sibling folder, then the new ones moved in; if any single move fails, every
    /// completed move is reversed, so the target ends up holding either the whole new set or exactly what
    /// it held before. Other files in the target (a licence copy, extra plugins) are never touched.
    /// </summary>
    /// <remarks>
    /// SP-0178: when the reversal fails too, the target may hold a mix of both builds that would otherwise
    /// count as installed - and a complete-looking set is what lets the next install sweep the preserved
    /// folder. The target is therefore marked incomplete (<see cref="FFmpegComponents.IncompleteMarkerName"/>)
    /// before the exception naming that folder is thrown, and the mark is cleared only by a publish that
    /// completes.
    /// </remarks>
    /// <param name="move">The file move; <see cref="File.Move(string, string)"/> unless a test injects a failure.</param>
    internal static void PublishStaging(string staging, string target, Action<string, string>? move = null)
    {
        move ??= File.Move;
        Directory.CreateDirectory(target);
        var previous = $"{target}{PreviousMarker}{Guid.NewGuid():N}";
        var movedAside = new List<string>();
        var movedIn = new List<string>();

        try
        {
            foreach (var library in FFmpegComponents.RequiredLibraries)
            {
                var current = Path.Combine(target, library);
                if (File.Exists(current))
                {
                    Directory.CreateDirectory(previous);
                    move(current, Path.Combine(previous, library));
                    movedAside.Add(library);
                }
            }

            foreach (var library in FFmpegComponents.RequiredLibraries)
            {
                move(Path.Combine(staging, library), Path.Combine(target, library));
                movedIn.Add(library);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            if (!TryRollBack(staging, target, previous, movedIn, movedAside, move))
            {
                MarkIncomplete(target, previous);
                throw new FFmpegComponentsRollbackException(
                    "The FFmpeg components could not be replaced, and the previous set could not be fully " +
                    $"restored; its libraries were kept in {previous}.",
                    previous,
                    failure);
            }

            DiscardFolder(previous);
            RemoveIfEmpty(target);
            throw;
        }

        // Best effort: the old set is no longer needed. A file that cannot be deleted now is swept by the
        // next install once a complete set is in place.
        DiscardFolder(previous);
        // A whole new set is in place, so an earlier failed rollback's mark no longer describes it.
        Discard(Path.Combine(target, FFmpegComponents.IncompleteMarkerName));
    }

    private static bool TryRollBack(
        string staging,
        string target,
        string previous,
        IEnumerable<string> movedIn,
        IEnumerable<string> movedAside,
        Action<string, string> move)
    {
        var restored = true;
        foreach (var library in movedIn)
        {
            restored &= TryMove(Path.Combine(target, library), Path.Combine(staging, library), move);
        }

        foreach (var library in movedAside)
        {
            restored &= TryMove(Path.Combine(previous, library), Path.Combine(target, library), move);
        }

        return restored;
    }

    /// <summary>
    /// Best effort: if even this write fails, the folder may still read as installed - but nothing more can
    /// be done from here, and the exception that follows still names the preserved folder.
    /// </summary>
    private static void MarkIncomplete(string target, string previous)
    {
        try
        {
            File.WriteAllText(Path.Combine(target, FFmpegComponents.IncompleteMarkerName), previous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryMove(string from, string to, Action<string, string> move)
    {
        try
        {
            move(from, to);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes what an interrupted earlier run (a crash, a killed process) left beside the target. A
    /// moved-aside previous set is only removed once a complete set is installed - until then it may be
    /// the only copy of the user's libraries.
    /// </summary>
    private static void SweepLeftovers(string dataDirectory, string target)
    {
        var name = Path.GetFileName(target);
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dataDirectory, $"{name}.*").ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var complete = FFmpegComponents.IsInstalled(target);
        foreach (var entry in entries)
        {
            var leaf = Path.GetFileName(entry);
            if (leaf.StartsWith(name + StagingMarker, StringComparison.OrdinalIgnoreCase)
                || (complete && leaf.StartsWith(name + PreviousMarker, StringComparison.OrdinalIgnoreCase)))
            {
                DiscardFolder(entry);
            }
            else if (leaf.StartsWith(name + DownloadMarker, StringComparison.OrdinalIgnoreCase))
            {
                Discard(entry);
            }
        }
    }

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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
            // A leftover temp file is not worth failing an otherwise complete install over; the next
            // install sweeps it.
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

    /// <summary>Synchronous forwarding: <see cref="Progress{T}"/> would post, and reorder, the reports.</summary>
    private sealed class ProgressRelay(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
