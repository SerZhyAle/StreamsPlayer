using System.Net;
using System.Net.Http;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class HttpDownloadTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private const int BodyBytes = 300 * 1024;

    [Fact]
    public async Task ReadAllBytes_ReportsABaselineWithTheTotalBeforeAnyBytesArrive()
    {
        var body = Body();
        using var response = Responding(new MemoryStream(body), body.Length);
        var reports = new List<DownloadProgress>();

        await HttpDownload.ReadAllBytesAsync(response, Collect(reports), null, Generous, default);

        Assert.Equal(new DownloadProgress(0, body.Length), reports[0]);
        Assert.Equal(0d, reports[0].Fraction);
    }

    [Fact]
    public async Task ReadAllBytes_ReportsUpToTheFullDeclaredLength()
    {
        var body = Body();
        using var response = Responding(new MemoryStream(body), body.Length);
        var reports = new List<DownloadProgress>();

        var bytes = await HttpDownload.ReadAllBytesAsync(response, Collect(reports), null, Generous, default);

        Assert.Equal(body, bytes);
        Assert.Equal(new DownloadProgress(body.Length, body.Length), reports[^1]);
        Assert.Equal(1d, reports[^1].Fraction);
        Assert.Equal(reports.Select(report => report.ReceivedBytes).Order(), reports.Select(report => report.ReceivedBytes));
    }

    [Fact]
    public async Task ReadAllBytes_LeavesTheFractionUnknownWhenTheServerDeclaresNoLength()
    {
        var body = Body();
        using var response = Responding(new MemoryStream(body), declaredLength: null);
        var reports = new List<DownloadProgress>();

        var bytes = await HttpDownload.ReadAllBytesAsync(response, Collect(reports), null, Generous, default);

        Assert.Equal(body, bytes);
        Assert.All(reports, report => Assert.Null(report.TotalBytes));
        Assert.All(reports, report => Assert.Null(report.Fraction));
    }

    [Fact]
    public async Task ReadAllBytes_RefusesAnOverDeclaredBodyWithoutReadingIt()
    {
        var body = Body();
        var source = new CountingStream(new MemoryStream(body));
        using var response = Responding(source, declaredLength: BodyBytes * 2);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HttpDownload.ReadAllBytesAsync(response, null, BodyBytes, Generous, default));

        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task ReadAllBytes_RefusesAnUnderDeclaredBodyOnceItOverrunsTheCeiling()
    {
        var body = Body();
        using var response = Responding(new MemoryStream(body), declaredLength: 1024);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HttpDownload.ReadAllBytesAsync(response, null, 64 * 1024, Generous, default));
    }

    [Fact]
    public async Task ReadAllBytes_RaisesATimeoutWhenTheTransferGoesSilent()
    {
        using var response = Responding(new StallingStream(Body(1024)), declaredLength: null);

        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            HttpDownload.ReadAllBytesAsync(response, null, null, TimeSpan.FromMilliseconds(200), default));

        Assert.IsNotType<OperationCanceledException>(failure);
    }

    [Fact]
    public async Task ReadAllBytes_DoesNotCutASlowButLiveTransfer()
    {
        // Fifteen chunks, 100 ms apart: the transfer runs past the 1 s idle bound while never being
        // silent for it. This is the whole of the decision - the bound is on silence, not on duration.
        //
        // The two margins are deliberately lopsided. Duration only ever grows - Task.Delay is a floor,
        // never a ceiling - so 1.5 s against a 1 s bound cannot shrink below it however loaded the
        // machine is. The silent gap is the fragile side, and it was 100 ms against 200 ms until
        // 2026-08-09, when a GitHub runner stretched one delay past the bound and failed this test
        // twice in a row on a commit that passed locally and in the release job. Ten times the drip
        // is the room that costs 0.9 s of test time and buys a gate that does not lie.
        var body = Body(15 * 1024);
        using var response = Responding(
            new DrippingStream(body, chunkBytes: 1024, delay: TimeSpan.FromMilliseconds(100)),
            declaredLength: body.Length);

        var bytes = await HttpDownload.ReadAllBytesAsync(
            response, null, null, TimeSpan.FromSeconds(1), default);

        Assert.Equal(body, bytes);
    }

    [Fact]
    public async Task ReadAllBytes_SurfacesACallersCancellationAsCancellationNotTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        using var response = Responding(new StallingStream(Body(1024), cancellation.Cancel), declaredLength: null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpDownload.ReadAllBytesAsync(response, null, null, Generous, cancellation.Token));
    }

    private static byte[] Body(int length = BodyBytes)
    {
        var body = new byte[length];
        for (var index = 0; index < body.Length; index++)
        {
            body[index] = (byte)index;
        }

        return body;
    }

    private static HttpResponseMessage Responding(Stream body, long? declaredLength)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static IProgress<DownloadProgress> Collect(List<DownloadProgress> reports) => new Reporter(reports.Add);

    private sealed class Reporter(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    /// <summary>Counts reads so a header-only refusal can be told from one that consumed the body.</summary>
    private sealed class CountingStream(Stream inner) : DelegatingReadStream
    {
        public int Reads { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return await inner.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>Delivers a first chunk, then never answers again - a socket that stopped talking.</summary>
    private sealed class StallingStream(byte[] first, Action? afterFirstChunk = null) : DelegatingReadStream
    {
        private bool _delivered;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delivered)
            {
                _delivered = true;
                first.AsMemory(0, Math.Min(first.Length, buffer.Length)).CopyTo(buffer);
                afterFirstChunk?.Invoke();
                return Math.Min(first.Length, buffer.Length);
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>Delivers the body in fixed chunks with a pause between them - a slow but live link.</summary>
    private sealed class DrippingStream(byte[] body, int chunkBytes, TimeSpan delay) : DelegatingReadStream
    {
        private int _offset;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= body.Length)
            {
                return 0;
            }

            await Task.Delay(delay, cancellationToken);
            var count = Math.Min(chunkBytes, Math.Min(buffer.Length, body.Length - _offset));
            body.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }

    /// <summary>The read-only, forward-only surface <see cref="StreamContent"/> actually uses.</summary>
    private abstract class DelegatingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
