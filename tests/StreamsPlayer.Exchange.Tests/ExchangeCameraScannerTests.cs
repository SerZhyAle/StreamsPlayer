using System.Runtime.InteropServices;
using StreamsPlayer.App;
using StreamsPlayer.Core;

namespace StreamsPlayer.Exchange.Tests;

/// <summary>
/// SP-0229: what a live camera's frames come to, proved without a camera. A frame is a picture like the ones the
/// picture doors read, so the known answers are the contract's own connection strings drawn the way a camera
/// sees a code on a screen (a small code in a large frame); the scan's own rules - a wrong code is a hint, the
/// first connection string ends it, one decode at a time, a time limit - are proved with a decoder that can be held.
/// </summary>
public sealed class ExchangeCameraScannerTests
{
    private const string Valid = "FMSEXCH1:x=exchange.example.org%3A22022&fp=SHA256%3AUl2rC03A44auS8B7ibLjJaAApHooIx%2F4R%2BSSgDTXUtY&n=Office&u=carol";

    private static ExchangePixels Frame(string text) =>
        QrPictures.Draw(QrPictures.Matrix(text), scale: 4, canvasWidth: 1280, canvasHeight: 720, offsetX: 420, offsetY: 190);

    [Theory]
    [MemberData(nameof(ExchangeQrReaderTests.ContractStrings), MemberType = typeof(ExchangeQrReaderTests))]
    public async Task ContractString_SeenByTheCamera_EndsTheScanWithTheSameFormThePastedStringFills(string name, string text)
    {
        var scanner = new ExchangeCameraScanner();

        var step = await scanner.OfferAsync(Frame(text));

        Assert.True(step.Kind == ExchangeCameraStepKind.Done, $"{name}: {step.Kind} {step.HintKey}");
        Assert.NotNull(step.Result);
        var pasted = ExchangeConnectionImport.ReadText(text);
        Assert.Equal(pasted.MessageKey, step.Result.MessageKey);
        Assert.Equal(pasted.Data?.Host, step.Result.Data?.Host);
        Assert.Equal(pasted.Data?.Port, step.Result.Data?.Port);
        Assert.Equal(pasted.Data?.Login, step.Result.Data?.Login);
        Assert.True(scanner.IsFinished);
    }

    [Fact]
    public async Task AnotherCode_IsAHint_TheScanGoesOn_AndTheNextConnectionStringEndsIt()
    {
        var scanner = new ExchangeCameraScanner();

        var poster = await scanner.OfferAsync(Frame("https://example.org/a/poster"));
        Assert.Equal(ExchangeCameraStepKind.Hint, poster.Kind);
        Assert.Equal(ExchangeCameraScanner.OtherCodeHint, poster.HintKey);
        Assert.False(scanner.IsFinished);

        var ours = await scanner.OfferAsync(Frame(Valid));
        Assert.Equal(ExchangeCameraStepKind.Done, ours.Kind);
        Assert.Equal("exchange.example.org", ours.Result!.Data!.Host);
        Assert.Equal("carol", ours.Result.Data.Login);
    }

    [Fact]
    public async Task TwoCodesInView_AreAHint_EvenWhenBothAreConnectionStrings()
    {
        var scanner = new ExchangeCameraScanner();
        var canvas = QrPictures.Blank(1280, 720, 255);
        QrPictures.Place(canvas, QrPictures.Matrix("FMSEXCH1:x=192.0.2.10%3A22022"), 4, 4, false, 60, 100);
        QrPictures.Place(canvas, QrPictures.Matrix("FMSEXCH1:x=192.0.2.11%3A22022"), 4, 4, false, 700, 100);

        var step = await scanner.OfferAsync(canvas);

        Assert.Equal(ExchangeCameraStepKind.Hint, step.Kind);
        Assert.Equal(ExchangeCameraScanner.SeveralHint, step.HintKey);
        Assert.False(scanner.IsFinished);
    }

    [Fact]
    public async Task AFrameWithNoCode_ContinuesWithoutAHint()
    {
        var scanner = new ExchangeCameraScanner();

        var step = await scanner.OfferAsync(QrPictures.Blank(1280, 720, 180));

        Assert.Equal(ExchangeCameraStepKind.Continue, step.Kind);
        Assert.Null(step.HintKey);
        Assert.Null(step.Result);
    }

    [Theory]
    [InlineData("FMSEXCH2:x=192.0.2.10%3A22022", "ExchangeFormatUnsupported")]
    [InlineData("FMSEXCH1:x=192.0.2.10%3A0", "ExchangeStringInvalid")]
    public async Task AStringTheCodecRefuses_EndsTheScanWithTheCodecsOneMessage(string text, string messageKey)
    {
        var scanner = new ExchangeCameraScanner();

        var step = await scanner.OfferAsync(Frame(text));

        Assert.Equal(ExchangeCameraStepKind.Done, step.Kind);
        Assert.Null(step.Result!.Data);
        Assert.Equal(messageKey, step.Result.MessageKey);
        Assert.True(scanner.IsFinished);
    }

    [Fact]
    public async Task AfterTheScanEnded_EveryFrameIsDropped()
    {
        var scanner = new ExchangeCameraScanner();
        Assert.Equal(ExchangeCameraStepKind.Done, (await scanner.OfferAsync(Frame(Valid))).Kind);

        Assert.Equal(ExchangeCameraStepKind.Dropped, (await scanner.OfferAsync(Frame(Valid))).Kind);
        Assert.Equal(ExchangeCameraStepKind.Dropped, (await scanner.OfferAsync(QrPictures.Blank(640, 480, 200))).Kind);
    }

    [Fact]
    public async Task AFrameOfferedWhileOneIsBeingRead_IsDropped_NotQueued()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoded = 0;
        var scanner = new ExchangeCameraScanner(decode: _ =>
        {
            Interlocked.Increment(ref decoded);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new ExchangeQrOutcome(ExchangeQrOutcomeKind.None);
        });

        var first = scanner.OfferAsync(QrPictures.Blank(64, 64, 128));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = await scanner.OfferAsync(QrPictures.Blank(64, 64, 128));
        release.Set();

        Assert.Equal(ExchangeCameraStepKind.Dropped, second.Kind);
        Assert.Equal(ExchangeCameraStepKind.Continue, (await first).Kind);
        Assert.Equal(1, decoded);
        // The reader is free again once the first frame is done.
        Assert.Equal(ExchangeCameraStepKind.Continue, (await scanner.OfferAsync(QrPictures.Blank(64, 64, 128))).Kind);
    }

    [Fact]
    public async Task ThePassingOfTheTimeLimit_EndsTheScanOnceAndDropsWhatFollows()
    {
        var clock = new ManualClock();
        var scanner = new ExchangeCameraScanner(TimeSpan.FromSeconds(120), clock);

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(ExchangeCameraStepKind.Continue, (await scanner.OfferAsync(QrPictures.Blank(64, 64, 128))).Kind);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(ExchangeCameraStepKind.TimedOut, (await scanner.OfferAsync(Frame(Valid))).Kind);
        Assert.True(scanner.IsFinished);
        Assert.Equal(ExchangeCameraStepKind.Dropped, (await scanner.OfferAsync(Frame(Valid))).Kind);
    }

    [Fact]
    public async Task ADecoderThatThrows_CostsThatFrameAndNothingElse()
    {
        var calls = 0;
        var scanner = new ExchangeCameraScanner(decode: pixels =>
            ++calls == 1 ? throw new InvalidOperationException("secret payload FMSEXCH1:") : ExchangeQrReader.Decode(pixels));

        var damaged = await scanner.OfferAsync(Frame(Valid));
        var next = await scanner.OfferAsync(Frame(Valid));

        Assert.Equal(ExchangeCameraStepKind.Continue, damaged.Kind);
        Assert.Null(damaged.HintKey);
        Assert.Equal(ExchangeCameraStepKind.Done, next.Kind);
    }

    [Fact]
    public async Task ACancelledOffer_IsDropped_AndStopEndsTheScan()
    {
        var scanner = new ExchangeCameraScanner();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Equal(ExchangeCameraStepKind.Dropped, (await scanner.OfferAsync(Frame(Valid), cancelled.Token)).Kind);

        scanner.Stop();

        Assert.True(scanner.IsFinished);
        Assert.Equal(ExchangeCameraStepKind.Dropped, (await scanner.OfferAsync(Frame(Valid))).Kind);
    }

    [Fact]
    public async Task AHostileFrame_NeverThrows()
    {
        var scanner = new ExchangeCameraScanner();

        Assert.Equal(ExchangeCameraStepKind.Continue, (await scanner.OfferAsync(new ExchangePixels([], 0, 0))).Kind);
        Assert.Equal(ExchangeCameraStepKind.Continue, (await scanner.OfferAsync(new ExchangePixels(new byte[16], 100_000, 100_000))).Kind);
        Assert.False(scanner.IsFinished);
    }

    [Fact]
    public void TheModeThatReadsADenseCodeBest_IsChosen()
    {
        ExchangeCameraFormat F(string subtype, int width, int height, double rate) => new(subtype, width, height, rate);

        // Uncompressed 720p beats compressed 1080p: the same width class, no decompression.
        Assert.Equal(0, ExchangeCameraFormats.Choose([F("NV12", 1280, 720, 30), F("MJPG", 1920, 1080, 30)]));
        // Wide enough comes before the format: a small uncompressed mode cannot resolve the code.
        Assert.Equal(1, ExchangeCameraFormats.Choose([F("YUY2", 320, 240, 60), F("MJPG", 1280, 720, 30)]));
        Assert.Equal(1, ExchangeCameraFormats.Choose([F("YUY2", 320, 240, 30), F("YUY2", 640, 480, 15)]));
        // A picture past the ceiling only costs decoding time, uncompressed or not.
        Assert.Equal(1, ExchangeCameraFormats.Choose([F("NV12", 3840, 2160, 30), F("MJPG", 1280, 720, 30)]));
        // Nearest the preferred width, then the faster mode.
        Assert.Equal(1, ExchangeCameraFormats.Choose([F("NV12", 1920, 1080, 30), F("NV12", 1280, 720, 15), F("NV12", 640, 480, 30)]));
        Assert.Equal(2, ExchangeCameraFormats.Choose([F("NV12", 1280, 720, 15), F("NV12", 640, 480, 30), F("NV12", 1280, 720, 30)]));
        // What the camera has is better than nothing.
        Assert.Equal(0, ExchangeCameraFormats.Choose([F("MJPG", 3840, 2160, 30)]));
        Assert.Equal(1, ExchangeCameraFormats.Choose([F("NV12", 160, 120, 30), F("NV12", 320, 240, 30)]));
        Assert.Equal(-1, ExchangeCameraFormats.Choose([]));
        Assert.Equal(-1, ExchangeCameraFormats.Choose([F("NV12", 0, 0, 30)]));
    }

    [Fact]
    public void AnAccessRefusal_ReadsAsDenied_AndAnyOtherFailureAsACameraThatCouldNotBeOpened()
    {
        Assert.Equal(ExchangeCameraFailure.AccessDenied, ExchangeCameraFailures.Classify(new UnauthorizedAccessException()));
        Assert.Equal(ExchangeCameraFailure.AccessDenied,
            ExchangeCameraFailures.Classify(new COMException("denied", unchecked((int)0x80070005))));
        Assert.Equal(ExchangeCameraFailure.CouldNotOpen, ExchangeCameraFailures.Classify(new COMException("busy", unchecked((int)0xC00D3704))));
        Assert.Equal(ExchangeCameraFailure.CouldNotOpen, ExchangeCameraFailures.Classify(new InvalidOperationException()));
        Assert.Equal("ExchangeCameraDenied", ExchangeCameraFailures.MessageKey(ExchangeCameraFailure.AccessDenied));
        Assert.Equal("ExchangeCameraBusy", ExchangeCameraFailures.MessageKey(ExchangeCameraFailure.CouldNotOpen));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal void Advance(TimeSpan by) => Interlocked.Add(ref _timestamp, (long)(by.TotalSeconds * TimestampFrequency));
    }
}
