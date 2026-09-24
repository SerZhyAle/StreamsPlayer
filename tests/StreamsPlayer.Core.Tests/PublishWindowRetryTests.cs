using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0107, STREAM-BANK rule 11 (amendment item H): the policy itself - what counts as the publish
/// window, how long the schedule is, and how it ends. The services that use it are gated in
/// <see cref="PublishWindowFetchTests"/>.
/// </summary>
public sealed class PublishWindowRetryTests
{
    private static readonly PublishWindowRetry Immediate = new([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

    [Fact]
    public void Classify_NamesTheThreeOutcomesTheContractCallsExpected()
    {
        Assert.Equal(PublishWindowCause.NotFound,
            PublishWindowRetry.Classify(new HttpRequestException("gone", null, HttpStatusCode.NotFound)));
        Assert.Equal(PublishWindowCause.ShortRead,
            PublishWindowRetry.Classify(new HttpIOException(HttpRequestError.ResponseEnded)));
        Assert.Equal(PublishWindowCause.TruncatedArchive,
            PublishWindowRetry.Classify(new TruncatedArchiveException("cut", new InvalidDataException())));
    }

    [Fact]
    public void Classify_FindsAShortReadWrappedByTheReadingLayer() =>
        Assert.Equal(PublishWindowCause.ShortRead, PublishWindowRetry.Classify(
            new IOException("read failed", new HttpIOException(HttpRequestError.ResponseEnded))));

    [Fact]
    public void Classify_LeavesEveryOtherFailureAnError()
    {
        Assert.Null(PublishWindowRetry.Classify(
            new HttpRequestException("server", null, HttpStatusCode.InternalServerError)));
        Assert.Null(PublishWindowRetry.Classify(new HttpRequestException("dns")));
        Assert.Null(PublishWindowRetry.Classify(new HttpIOException(HttpRequestError.ConnectionError)));
        Assert.Null(PublishWindowRetry.Classify(new InvalidDataException("ceiling or bad CSV")));
        Assert.Null(PublishWindowRetry.Classify(new TimeoutException()));
        Assert.Null(PublishWindowRetry.Classify(new OperationCanceledException()));
    }

    /// <summary>The window is seconds, so the schedule is too - bounded, short, never open-ended.</summary>
    [Fact]
    public void DefaultSchedule_IsBoundedAndShort()
    {
        var schedule = PublishWindowRetry.Default;

        Assert.Equal(schedule.Delays.Count + 1, schedule.MaximumAttempts);
        Assert.InRange(schedule.Delays.Count, 1, 5);
        Assert.All(schedule.Delays, delay => Assert.True(delay > TimeSpan.Zero));
        Assert.True(schedule.Delays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay) <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Run_RetriesThePublishWindowAndReportsEachRetry()
    {
        var calls = 0;
        var notices = new List<PublishWindowRetryNotice>();

        var result = await Immediate.RunAsync(
            _ => ++calls < 3 ? throw NotFound() : Task.FromResult("bank"),
            new Collector<PublishWindowRetryNotice>(notices),
            CancellationToken.None);

        Assert.Equal("bank", result);
        Assert.Equal(3, calls);
        Assert.Equal([2, 3], notices.Select(notice => notice.NextAttempt));
        Assert.All(notices, notice => Assert.Equal(4, notice.MaximumAttempts));
        Assert.All(notices, notice => Assert.Equal(PublishWindowCause.NotFound, notice.Cause));
    }

    [Fact]
    public async Task Run_SurfacesTheLastFailureOnceTheScheduleIsSpent()
    {
        var calls = 0;
        HttpRequestException? last = null;

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Immediate.RunAsync<string>(
            _ =>
            {
                calls++;
                throw last = NotFound();
            },
            null,
            CancellationToken.None));

        Assert.Equal(Immediate.MaximumAttempts, calls);
        Assert.Same(last, thrown);
    }

    [Fact]
    public async Task Run_DoesNotRepeatAFailureOutsideTheWindow()
    {
        var calls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => Immediate.RunAsync<string>(
            _ =>
            {
                calls++;
                throw new InvalidDataException("above the ceiling");
            },
            null,
            CancellationToken.None));

        Assert.Equal(1, calls);
    }

    /// <summary>The retry belongs to the user's operation: cancelling it ends the wait, not just the next try.</summary>
    [Fact]
    public async Task Run_CancellingTheOperationEndsTheWait()
    {
        using var operation = new CancellationTokenSource();
        var patient = new PublishWindowRetry([TimeSpan.FromMinutes(5)]);
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patient.RunAsync<string>(
            _ =>
            {
                calls++;
                throw NotFound();
            },
            new Collector<PublishWindowRetryNotice>([], operation.Cancel),
            operation.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HttpDownload_RaisesAShortReadWhenTheBodyEndsBeforeItsDeclaredLength()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ShortContent(new byte[1000], declaredLength: 4000)
        };

        var error = await Assert.ThrowsAsync<HttpIOException>(() => HttpDownload.ReadAllBytesAsync(
            response, null, null, TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Equal(HttpRequestError.ResponseEnded, error.HttpRequestError);
    }

    /// <summary>
    /// Once the retries are spent, a cut-off archive still tells the user the data was damaged - the
    /// wrapper must not turn it into an unknown failure or, worse, a storage one.
    /// </summary>
    [Fact]
    public void TruncatedArchive_StillReadsAsDamagedDataToTheUser() =>
        Assert.Equal(FailureCause.DamagedData, FailureCauseClassifier.Classify(
            new TruncatedArchiveException("cut", new InvalidDataException("End of Central Directory"))));

    internal static HttpRequestException NotFound() =>
        new("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound);

    internal static byte[] BankZip(string csv)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("streams.csv").Open();
            stream.Write(Encoding.UTF8.GetBytes(csv));
        }

        return buffer.ToArray();
    }

    /// <summary>Synchronous, unlike <see cref="Progress{T}"/>, so a test can act inside the report.</summary>
    internal sealed class Collector<T>(List<T> into, Action? onReport = null) : IProgress<T>
    {
        public void Report(T value)
        {
            into.Add(value);
            onReport?.Invoke();
        }
    }

    /// <summary>Declares more than it sends - what a body cut off by a delete looks like on the wire.</summary>
    internal sealed class ShortContent(byte[] body, long declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }
}
