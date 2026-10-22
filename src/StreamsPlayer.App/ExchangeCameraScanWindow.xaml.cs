using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0229: the camera door of the first-connection prompt. It shows what the camera sees, reads one frame at a
/// time with the reader of the picture doors, and closes with a result at the first connection string - reading is
/// never connecting. The camera is opened only when this window is, and released the moment it closes, ends, or
/// the person picks another camera. Pictures are copied through memory to the screen and to the reader; none is
/// stored, sent or logged.
/// </summary>
public partial class ExchangeCameraScanWindow : Window
{
    private const int PreviewIntervalMilliseconds = 160;
    private const int DecodeIntervalMilliseconds = 320;
    private const int FirstPictureSeconds = 8;
    private const int HintSeconds = 3;

    private readonly IReadOnlyList<ExchangeCameraInfo> _cameras;
    private readonly ExchangeCameraScanner _scanner = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private ExchangeCameraSession? _session;
    private WriteableBitmap? _bitmap;
    private bool _initializing = true;
    // Read on the session's frame thread as well as the UI thread.
    private volatile bool _ended;
    private bool _pictureSeen;
    private int _run;
    private DateTime _startedAt;
    private DateTime _hintShownAt = DateTime.MinValue;
    // Touched only inside the session's frame callback, which is serialized by the session's lock.
    private long _lastPreviewAt;
    private long _lastDecodeAt;

    /// <summary>What the scan came to: the string's data, or the codec's message. Null when the window was closed without a result.</summary>
    internal ExchangeImportResult? Result { get; private set; }

    internal ExchangeCameraScanWindow(IReadOnlyList<ExchangeCameraInfo> cameras)
    {
        _cameras = cameras;
        InitializeComponent();
        CameraBox.ItemsSource = cameras;
        CameraBox.SelectedIndex = cameras.Count > 0 ? 0 : -1;
        ChooserRow.Visibility = cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _initializing = false;
        _watchdog.Tick += (_, _) => WatchFirstPicture();
        Loaded += (_, _) => StartSelected();
        Closed += (_, _) => Release();
    }

    private void Camera_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && IsLoaded)
        {
            StartSelected();
        }
    }

    private void StartSelected()
    {
        if (CameraBox.SelectedItem is ExchangeCameraInfo camera && !_ended)
        {
            HandlerBoundary.Run(nameof(StartAsync), () => StartAsync(camera));
        }
    }

    private async Task StartAsync(ExchangeCameraInfo camera)
    {
        StopSession();
        var run = ++_run;
        _pictureSeen = false;
        _startedAt = DateTime.UtcNow;
        StatusText.Text = LocalizationService.Get("ExchangeCameraStarting");
        _watchdog.Start();
        try
        {
            var session = await ExchangeCameraSession.StartAsync(camera, (bgra, width, height) => OnFrame(run, bgra, width, height));
            if (_ended || run != _run)
            {
                session.Dispose();
                return;
            }

            _session = session;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Windows reports a refused or busy camera with several exception types; the person is told which of
            // the two it is, and neither the exception's text nor the device's name goes anywhere.
            if (!_ended && run == _run)
            {
                End(ExchangeCameraFailures.MessageKey(ExchangeCameraFailures.Classify(exception)));
            }
        }
    }

    // Runs on a thread-pool thread inside the session's lock: copies what it keeps and returns.
    private void OnFrame(int run, byte[] bgra, int width, int height)
    {
        if (_ended || run != Volatile.Read(ref _run))
        {
            return;
        }

        var now = Environment.TickCount64;
        var length = width * height * 4;
        if (now - _lastPreviewAt >= PreviewIntervalMilliseconds)
        {
            _lastPreviewAt = now;
            var copy = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(bgra, 0, copy, 0, length);
            Dispatcher.InvokeAsync(() => ShowPreview(run, copy, width, height));
        }

        if (!_scanner.IsFinished && !_scanner.IsBusy && now - _lastDecodeAt >= DecodeIntervalMilliseconds)
        {
            _lastDecodeAt = now;
            var pixels = new ExchangePixels(bgra.AsSpan(0, length).ToArray(), width, height);
            HandlerBoundary.Run(nameof(ScanAsync), () => ScanAsync(run, pixels));
        }
    }

    private void ShowPreview(int run, byte[] buffer, int width, int height)
    {
        try
        {
            if (_ended || run != _run)
            {
                return;
            }

            // Bgr32, not Bgra32: a converted camera frame need not carry a meaningful alpha, and a zero one would
            // paint the picture transparent.
            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                Preview.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 4, 0);
            if (!_pictureSeen)
            {
                _pictureSeen = true;
                _watchdog.Stop();
                StatusText.Text = "";
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ScanAsync(int run, ExchangePixels pixels)
    {
        var step = await _scanner.OfferAsync(pixels, _lifetime.Token).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() => ApplyStep(run, step));
    }

    private void ApplyStep(int run, ExchangeCameraStep step)
    {
        if (_ended || run != _run)
        {
            return;
        }

        switch (step.Kind)
        {
            case ExchangeCameraStepKind.Done:
                Result = step.Result;
                _ended = true;
                StopSession();
                DialogResult = true;
                break;
            case ExchangeCameraStepKind.Hint:
                StatusText.Text = LocalizationService.Get(step.HintKey!);
                _hintShownAt = DateTime.UtcNow;
                break;
            case ExchangeCameraStepKind.TimedOut:
                // The scan itself is over, so choosing another camera would only run into the same end.
                CameraBox.IsEnabled = false;
                End("ExchangeCameraTimeout");
                break;
            case ExchangeCameraStepKind.Continue:
                // A hint stays long enough to read, then the line is clear again.
                if (_pictureSeen && DateTime.UtcNow - _hintShownAt > TimeSpan.FromSeconds(HintSeconds))
                {
                    StatusText.Text = "";
                }

                break;
        }
    }

    private void WatchFirstPicture()
    {
        if (_ended || _pictureSeen)
        {
            return;
        }

        // A camera that is still opening after this long gives the same answer as one that opened and stayed black.
        if (DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(FirstPictureSeconds))
        {
            End("ExchangeCameraNoPicture");
        }
    }

    /// <summary>The camera stops, the window stays so the person can read why; Cancel becomes Close.</summary>
    private void End(string messageKey)
    {
        StopSession();
        _watchdog.Stop();
        StatusText.Text = LocalizationService.Get(messageKey);
        CancelButton.Content = LocalizationService.Get("Close");
    }

    private void StopSession()
    {
        _run++;
        _session?.Dispose();
        _session = null;
    }

    private void Release()
    {
        _ended = true;
        _watchdog.Stop();
        // The session first: once it is disposed no frame callback is running or will run, so nothing below races one.
        _session?.Dispose();
        _session = null;
        _scanner.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
