using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class FFmpegComponentsInstallerTests
{
    private static readonly TimeSpan ShortIdle = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Install_ExtractsEveryRequiredLibraryFromTheNestedBuildFolder()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));

            var folder = await installer.InstallAsync(directory);

            Assert.Equal(FFmpegComponents.ResolveFolder(directory), folder);
            Assert.True(FFmpegComponents.IsInstalled(folder));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_SkipsTheBundledExecutables()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));

            var folder = await installer.InstallAsync(directory);

            Assert.Equal(
                FFmpegComponents.RequiredLibraries.OrderBy(name => name),
                Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(name => name));
        });
    }

    [Fact]
    public async Task Install_ReportsProgressUpToTheDeclaredTotal()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));
            var reports = new List<FFmpegInstallProgress>();

            await installer.InstallAsync(directory, new Progress(reports.Add));

            Assert.NotEmpty(reports);
            Assert.Equal(archive.Length, reports[^1].ReceivedBytes);
            Assert.Equal(archive.Length, reports[^1].TotalBytes);
            Assert.Equal(1d, reports[^1].Fraction);
        });
    }

    [Fact]
    public async Task Install_FailsAndInstallsNothingWhenALibraryIsMissingFromTheArchive()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var absent = FFmpegComponents.RequiredLibraries[2];
            var archive = CreateArchive(omit: absent);
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => installer.InstallAsync(directory));

            Assert.Contains(absent, error.Message);
            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_WithAWrongDigest_InstallsNothing()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var pinned = SourceFor(archive) with { Sha256 = new string('0', 64) };
            var installer = new FFmpegComponentsInstaller(httpClient, pinned);

            var error = await Assert.ThrowsAsync<FFmpegArchiveMismatchException>(() => installer.InstallAsync(directory));

            Assert.Contains("digest", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_WithAWrongDigest_LeavesThePreviousSetIntact()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var folder = FFmpegComponents.ResolveFolder(directory);
            WriteOldSet(folder);
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(
                httpClient, SourceFor(archive) with { Sha256 = new string('f', 64) });

            await Assert.ThrowsAsync<FFmpegArchiveMismatchException>(() => installer.InstallAsync(directory));

            AssertOldSet(folder);
        });
    }

    [Fact]
    public async Task Install_RefusesADeclaredLengthOtherThanThePinnedOneBeforeWritingAnything()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = Serving(archive, archive.Length + 1);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));

            await Assert.ThrowsAsync<FFmpegArchiveMismatchException>(() => installer.InstallAsync(directory));

            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_RefusesABodyLongerThanThePinnedLengthWhenNoneIsDeclared()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = new HttpClient(new StubHandler(
                () => new UndeclaredLengthContent(archive), HttpStatusCode.OK));
            var installer = new FFmpegComponentsInstaller(
                httpClient, SourceFor(archive) with { Length = archive.Length - 1 });

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(directory));

            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_FailsOnAnErrorResponse()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            using var httpClient = new HttpClient(new StubHandler(
                () => new ByteArrayContent([]), HttpStatusCode.NotFound));
            var installer = new FFmpegComponentsInstaller(httpClient);

            await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(directory));

            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
        });
    }

    [Fact]
    public async Task Install_FailsAfterTheIdleBoundWhenTheBodyStalls()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = new HttpClient(new StubHandler(
                () => Declared(new StreamContent(new StallingStream(archive[..16])), archive.Length),
                HttpStatusCode.OK));
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive), ShortIdle);

            var started = DateTime.UtcNow;
            await Assert.ThrowsAsync<TimeoutException>(() => installer.InstallAsync(directory));

            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_FailsAfterTheIdleBoundWhenNoHeadersArrive()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = new HttpClient(new SilentHandler());
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive), ShortIdle);

            await Assert.ThrowsAsync<TimeoutException>(() => installer.InstallAsync(directory));

            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_CancelledMidDownload_LeavesNoStagedFiles()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var archive = CreateArchive();
            using var httpClient = new HttpClient(new StubHandler(
                () => Declared(new StreamContent(new StallingStream(archive[..16])), archive.Length),
                HttpStatusCode.OK));
            // A long idle bound, so only the user's cancel can end the transfer.
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive), TimeSpan.FromMinutes(5));
            using var cancel = new CancellationTokenSource();
            var progress = new Progress(report =>
            {
                if (report.ReceivedBytes > 0)
                {
                    cancel.Cancel();
                }
            });

            var error = await Record.ExceptionAsync(() => installer.InstallAsync(directory, progress, cancel.Token));

            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_ReplacesAnIncompleteExistingSet()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var folder = FFmpegComponents.ResolveFolder(directory);
            FFmpegComponentsTests.WriteAll(folder, except: FFmpegComponents.RequiredLibraries[1]);
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));

            await installer.InstallAsync(directory);

            Assert.True(FFmpegComponents.IsInstalled(folder));
            Assert.All(FFmpegComponents.RequiredLibraries, library =>
                Assert.Equal(NewContent, File.ReadAllBytes(Path.Combine(folder, library))));
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public async Task Install_KeepsUnrelatedFilesInTheComponentsFolder()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var folder = FFmpegComponents.ResolveFolder(directory);
            WriteOldSet(folder);
            var licence = Path.Combine(folder, "LICENSE.txt");
            File.WriteAllText(licence, "kept");
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));

            await installer.InstallAsync(directory);

            Assert.Equal("kept", File.ReadAllText(licence));
        });
    }

    [Fact]
    public async Task Install_WhenAMoveFails_LeavesThePreviousSetIntact()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var folder = FFmpegComponents.ResolveFolder(directory);
            WriteOldSet(folder);
            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            var installer = new FFmpegComponentsInstaller(httpClient, SourceFor(archive));

            // A handle without delete sharing is what a process holding the library open looks like to a
            // move: the fourth library cannot be moved aside after the first three already were.
            var locked = Path.Combine(folder, FFmpegComponents.RequiredLibraries[3]);
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => installer.InstallAsync(directory));
            }

            AssertOldSet(folder);
            AssertNothingStaged(directory);
        });
    }

    [Fact]
    public void PublishStaging_WhenAnIncomingMoveFails_RestoresThePreviousSet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var target = FFmpegComponents.ResolveFolder(directory);
            WriteOldSet(target);
            var staging = Path.Combine(directory, "staging");
            Directory.CreateDirectory(staging);
            // The last library is absent from the staging folder, so the second pass fails after six
            // libraries of the new set were already moved in.
            foreach (var library in FFmpegComponents.RequiredLibraries.SkipLast(1))
            {
                File.WriteAllBytes(Path.Combine(staging, library), NewContent);
            }

            Assert.ThrowsAny<IOException>(() => FFmpegComponentsInstaller.PublishStaging(staging, target));

            AssertOldSet(target);
            Assert.Empty(Directory.GetDirectories(directory, "FFmpeg.previous-*"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void PublishStaging_WhenTheRollbackFailsToo_ReportsNotInstalledAndKeepsThePreservedFolder()
    {
        // SP-0178: a move-in failure followed by a failed rollback used to leave a mix of both builds that
        // counted as installed, which let the next install sweep the folder the exception promised to keep.
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            var target = FFmpegComponents.ResolveFolder(directory);
            WriteOldSet(target);
            var staging = Path.Combine(directory, "staging");
            Directory.CreateDirectory(staging);
            foreach (var library in FFmpegComponents.RequiredLibraries)
            {
                File.WriteAllBytes(Path.Combine(staging, library), NewContent);
            }

            var libraries = FFmpegComponents.RequiredLibraries;
            // The fourth library cannot be moved in, and the first new library cannot be moved back out -
            // so the old first library cannot return either, and all seven names are present in the target.
            void Move(string from, string to)
            {
                if (string.Equals(from, Path.Combine(staging, libraries[3]), StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(from, Path.Combine(target, libraries[0]), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(to, Path.Combine(staging, libraries[0]), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new IOException("injected");
                }

                File.Move(from, to);
            }

            var failure = Assert.Throws<FFmpegComponentsRollbackException>(
                () => FFmpegComponentsInstaller.PublishStaging(staging, target, Move));

            Assert.All(libraries, library => Assert.True(File.Exists(Path.Combine(target, library))));
            Assert.False(FFmpegComponents.IsInstalled(target));
            Assert.True(File.Exists(Path.Combine(failure.PreservedFolder, libraries[0])));
            Assert.Equal(failure.PreservedFolder,
                File.ReadAllText(Path.Combine(target, FFmpegComponents.IncompleteMarkerName)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Install_AfterAFailedRollback_KeepsThePreservedFolderAndClearsTheMark()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var folder = FFmpegComponents.ResolveFolder(directory);
            WriteOldSet(folder);
            File.WriteAllText(Path.Combine(folder, FFmpegComponents.IncompleteMarkerName), "preserved");
            var preserved = Directory.CreateDirectory($"{folder}.previous-{Guid.NewGuid():N}").FullName;
            File.WriteAllBytes(Path.Combine(preserved, FFmpegComponents.RequiredLibraries[0]), [1]);
            Assert.False(FFmpegComponents.IsInstalled(folder));

            var archive = CreateArchive();
            using var httpClient = Serving(archive);
            await new FFmpegComponentsInstaller(httpClient, SourceFor(archive)).InstallAsync(directory);

            // The sweep ran before the install, while the set was still marked incomplete.
            Assert.True(Directory.Exists(preserved));
            Assert.True(FFmpegComponents.IsInstalled(folder));
            Assert.False(File.Exists(Path.Combine(folder, FFmpegComponents.IncompleteMarkerName)));
        });
    }

    [Fact]
    public void PinnedSource_IsAFixedLgplBuildWithADigest()
    {
        // Guards the licence decision in the strategic ticket: a -gpl- asset would place the user's
        // installation under GPLv3 terms the product does not carry.
        var source = FFmpegComponentsInstaller.PinnedSource;
        Assert.Contains("lgpl", source.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("-gpl-", source.Url, StringComparison.Ordinal);
        // SP-0128: a moving address cannot carry a pinned digest.
        Assert.DoesNotContain("/latest/", source.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("-latest-", source.Url, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", source.Sha256);
        Assert.True(source.Length > 0);
    }

    private static readonly byte[] NewContent = [0x4d, 0x5a, 0x90, 0x00];
    private static readonly byte[] OldContent = [0x4d, 0x5a, 0x01];

    private static void WriteOldSet(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var library in FFmpegComponents.RequiredLibraries)
        {
            File.WriteAllBytes(Path.Combine(folder, library), OldContent);
        }
    }

    private static void AssertOldSet(string folder) =>
        Assert.All(FFmpegComponents.RequiredLibraries, library =>
            Assert.Equal(OldContent, File.ReadAllBytes(Path.Combine(folder, library))));

    /// <summary>Nothing but the components folder itself may remain beside it after any exit.</summary>
    private static void AssertNothingStaged(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .Where(name => name != FFmpegComponents.FolderName));
    }

    private static FFmpegComponentsSource SourceFor(byte[] archive) =>
        new("https://example.invalid/ffmpeg-lgpl-shared.zip", archive.Length,
            Convert.ToHexStringLower(SHA256.HashData(archive)));

    private static HttpClient Serving(byte[] archive, long? declaredLength = null) =>
        new(new StubHandler(
            () => Declared(new ByteArrayContent(archive), declaredLength ?? archive.Length),
            HttpStatusCode.OK));

    private static HttpContent Declared(HttpContent content, long length)
    {
        content.Headers.ContentLength = length;
        return content;
    }

    private static byte[] CreateArchive(string? omit = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            const string root = "ffmpeg-n8.1.2-50-g1a748fe2cd-win64-lgpl-shared-8.1";
            foreach (var library in FFmpegComponents.RequiredLibraries)
            {
                if (library == omit)
                {
                    continue;
                }

                using var stream = archive.CreateEntry($"{root}/bin/{library}").Open();
                stream.Write(NewContent);
            }

            // The real asset also carries these; the installer must leave them alone.
            foreach (var executable in new[] { "ffmpeg.exe", "ffplay.exe", "ffprobe.exe" })
            {
                using var stream = archive.CreateEntry($"{root}/bin/{executable}").Open();
                stream.Write([0x4d, 0x5a]);
            }

            using (var stream = archive.CreateEntry($"{root}/LICENSE.txt").Open())
            {
                stream.Write([0x4c]);
            }
        }

        return buffer.ToArray();
    }

    private static async Task WithDataDirectoryAsync(Func<string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamsPlayer.Tests.{Guid.NewGuid():N}");
        try
        {
            await body(directory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class Progress(Action<FFmpegInstallProgress> report) : IProgress<FFmpegInstallProgress>
    {
        public void Report(FFmpegInstallProgress value) => report(value);
    }

    private sealed class StubHandler(Func<HttpContent> content, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content() });
    }

    /// <summary>A body sent without Content-Length, as a chunked response would be.</summary>
    private sealed class UndeclaredLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(body, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A server that accepts the request and never answers it.</summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    /// <summary>Delivers a first chunk, then holds the connection open and sends nothing more.</summary>
    private sealed class StallingStream(byte[] first) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                return first.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
