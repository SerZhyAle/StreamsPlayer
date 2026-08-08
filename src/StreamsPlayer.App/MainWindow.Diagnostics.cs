using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0040: the user-initiated log report, and the audio side of the playback-quality trace.
/// </summary>
/// <remarks>
/// The video path reports its own session summary from <see cref="PlayerWindow"/>. Audio plays inside
/// this window, so its accounting lives here: one summary per station session, in the same field shape,
/// minus the stall fields - the WPF media element exposes no buffer level and no position telemetry, so
/// "it stuttered for four seconds" is not knowable for audio and must not be implied.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Packs both session logs plus the environment summary and hands them to the user's mail program.
    /// Explicit, one-shot, and visible before sending - the user attaches and sends, or does not.
    /// </summary>
    private async Task SendLogsToAuthorAsync(Window owner)
    {
        if (DiagnosticLogFiles.ExistingLogs(_dataDirectory).Count == 0)
        {
            // Only reachable when local storage denied the log itself; without this the user would get an
            // archive holding nothing but the summary and no hint that the interesting half is missing.
            MessageBox.Show(owner, LocalizationService.Get("SendLogsNoLogs"), LocalizationService.Get("SendLogs"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var summary = DiagnosticEnvironmentSummary.Render(DiagnosticEnvironmentSummary.From(
            _state,
            ProductInfo.Version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            DateTimeOffset.UtcNow));

        var outputFolder = CapturedFrameWriter.ResolveFolder(_state.FrameFolder);
        string archivePath;
        try
        {
            // Copies and compresses files - off the UI thread even though the logs are small, because the
            // ceiling is 2 MB per log and this runs while the Settings window is open.
            archivePath = await Task.Run(() =>
                DiagnosticArchiveBuilder.Build(_dataDirectory, outputFolder, summary, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Event("LOG REPORT", "ok=false", $"err={exception.GetType().Name}");
            MessageBox.Show(owner, LocalizationService.Format("SendLogsFailed", outputFolder, exception.Message),
                LocalizationService.Get("SendLogs"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fileName = Path.GetFileName(archivePath);
        _log.Event("LOG REPORT", "ok=true", $"bytes={new FileInfo(archivePath).Length}", $"file={fileName}");
        var composed = LogReportMailer.Compose(
            ProductInfo.AuthorEmail,
            LocalizationService.Format("SendLogsSubject", ProductInfo.Version),
            LocalizationService.Format("SendLogsBody", archivePath));
        if (!composed)
        {
            MessageBox.Show(owner, LocalizationService.Format("SendLogsNoMailClient", archivePath),
                LocalizationService.Get("SendLogs"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new LogArchiveReadyWindow(archivePath) { Owner = owner }.ShowDialog();
    }

    /// <summary>
    /// SP-0067: opens a measurement of one catalog-list operation. Pair it with
    /// <see cref="CatalogPerf"/>, which stamps the elapsed milliseconds and writes the record.
    /// </summary>
    /// <remarks>
    /// A start/stop pair rather than the <c>IDisposable</c> the tactical plan sketched. Two of the four
    /// measured paths only know their interesting field at the end - <c>shown=</c> for the filter,
    /// <c>reused=</c> for the re-chunk - and a <c>using var</c> local is read-only, so calling a mutating
    /// method on a struct measurement would silently record the field into a defensive copy. A class
    /// would fix that by allocating on a path that runs per scroll event, which is what this measures.
    /// <see cref="Stopwatch.GetTimestamp"/> keeps the open side free of allocation either way.
    /// </remarks>
    private static long BeginCatalogPerf() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Closes a measurement opened by <see cref="BeginCatalogPerf"/>. Every caller must reach this on
    /// each exit path; an early <c>return</c> that skips it drops the sample rather than reporting zero.
    /// </summary>
    private void CatalogPerf(string operation, long startedAt, params string[] fields) =>
        _log.Event("CATALOG PERF",
            [$"op={operation}", .. fields, $"ms={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1}"]);

    private readonly Stopwatch _audioSessionClock = new();
    private string? _audioSessionUrl;
    private int _audioLegCount;
    private int _audioReconnectCount;
    private long _audioFirstLiveMs = -1;
    private string _audioSessionOutcome = "closed";

    private void BeginAudioSession(StreamChannel channel)
    {
        _audioSessionUrl = channel.Url;
        _audioLegCount = 1;
        _audioReconnectCount = 0;
        _audioFirstLiveMs = -1;
        _audioSessionOutcome = "closed";
        _audioSessionClock.Restart();
    }

    private void NoteAudioReconnectLeg()
    {
        _audioLegCount++;
        _audioReconnectCount++;
    }

    private void NoteAudioLive()
    {
        if (_audioFirstLiveMs < 0)
        {
            _audioFirstLiveMs = _audioSessionClock.ElapsedMilliseconds;
        }

        _audioSessionOutcome = "live";
    }

    private void NoteAudioTerminalFailure() => _audioSessionOutcome = "failed";

    private void EndAudioSession()
    {
        if (_audioSessionUrl is not { } url)
        {
            return; // no station session is open - a stop on an idle player records nothing
        }

        _audioSessionClock.Stop();
        _log.Event("AUDIO SESSION",
            $"session_ms={_audioSessionClock.ElapsedMilliseconds}",
            $"outcome={(_audioSessionOutcome == "closed" && _audioFirstLiveMs < 0 ? "never_live" : _audioSessionOutcome)}",
            $"ttff_ms={_audioFirstLiveMs}",
            $"legs={_audioLegCount}",
            $"reconnects={_audioReconnectCount}",
            $"url={url}");
        _audioSessionUrl = null;
    }
}
