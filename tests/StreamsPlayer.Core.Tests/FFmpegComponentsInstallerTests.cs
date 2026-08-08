using System.IO.Compression;
using System.Net;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class FFmpegComponentsInstallerTests
{
    [Fact]
    public async Task Install_ExtractsEveryRequiredLibraryFromTheNestedBuildFolder()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            using var httpClient = Serving(CreateArchive());
            var installer = new FFmpegComponentsInstaller(httpClient);

            var folder = await installer.InstallAsync(directory);

            Assert.Equal(FFmpegComponents.ResolveFolder(directory), folder);
            Assert.True(FFmpegComponents.IsInstalled(folder));
        });
    }

    [Fact]
    public async Task Install_SkipsTheBundledExecutables()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            using var httpClient = Serving(CreateArchive());
            var installer = new FFmpegComponentsInstaller(httpClient);

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
            var installer = new FFmpegComponentsInstaller(httpClient);
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
            using var httpClient = Serving(CreateArchive(omit: absent));
            var installer = new FFmpegComponentsInstaller(httpClient);

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => installer.InstallAsync(directory));

            Assert.Contains(absent, error.Message);
            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
        });
    }

    [Fact]
    public async Task Install_RefusesAnArchiveOverTheCeilingBeforeWritingAnything()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            using var httpClient = Serving(CreateArchive(), FFmpegComponentsInstaller.MaximumArchiveBytes + 1);
            var installer = new FFmpegComponentsInstaller(httpClient);

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(directory));

            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
        });
    }

    [Fact]
    public async Task Install_FailsOnAnErrorResponse()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            using var httpClient = new HttpClient(new StubHandler([], null, HttpStatusCode.NotFound));
            var installer = new FFmpegComponentsInstaller(httpClient);

            await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(directory));

            Assert.False(Directory.Exists(FFmpegComponents.ResolveFolder(directory)));
        });
    }

    [Fact]
    public async Task Install_ReplacesAnIncompleteExistingSet()
    {
        await WithDataDirectoryAsync(async directory =>
        {
            var folder = FFmpegComponents.ResolveFolder(directory);
            FFmpegComponentsTests.WriteAll(folder, except: FFmpegComponents.RequiredLibraries[1]);
            using var httpClient = Serving(CreateArchive());
            var installer = new FFmpegComponentsInstaller(httpClient);

            await installer.InstallAsync(directory);

            Assert.True(FFmpegComponents.IsInstalled(folder));
        });
    }

    [Fact]
    public void SourceUrl_PointsAtAnLgplBuild()
    {
        // Guards the licence decision in the strategic ticket: a -gpl- asset would place the user's
        // installation under GPLv3 terms the product does not carry.
        Assert.Contains("lgpl", FFmpegComponentsInstaller.SourceUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("-gpl-", FFmpegComponentsInstaller.SourceUrl, StringComparison.Ordinal);
    }

    private static HttpClient Serving(byte[] archive, long? declaredLength = null) =>
        new(new StubHandler(archive, declaredLength, HttpStatusCode.OK));

    private static byte[] CreateArchive(string? omit = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            const string root = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1";
            foreach (var library in FFmpegComponents.RequiredLibraries)
            {
                if (library == omit)
                {
                    continue;
                }

                using var stream = archive.CreateEntry($"{root}/bin/{library}").Open();
                stream.Write([0x4d, 0x5a, 0x90, 0x00]);
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

    private sealed class StubHandler(byte[] archive, long? declaredLength, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(archive);
            if (declaredLength is not null)
            {
                content.Headers.ContentLength = declaredLength;
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }
}
