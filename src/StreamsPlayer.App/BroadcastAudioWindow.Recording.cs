using System.IO;
using System.Windows;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class BroadcastAudioWindow
{
    private readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _recordBusy;
    private bool _closed;
    private CurrentLog? _recordLog;
    private Func<IReadOnlyList<string>>? _recordFolders;
    private string? _recordingFolder;

    internal event Action<string, object?[]>? RecordingNotice;

    internal void ConfigureRecording(CurrentLog log, Func<IReadOnlyList<string>> folders)
    {
        _recordLog = log;
        _recordFolders = folders;
        _session.RecordingCompleted += RecordingCompleted;
        RefreshRecording();
    }

    private void Record_Click(object sender, RoutedEventArgs e) => HandlerBoundary.Run(nameof(Record_Click), async () =>
    {
        if (_closed || _recordBusy || _recordLog is null || _recordFolders is null) return;
        _recordBusy = true;
        RefreshRecording();
        try
        {
            if (_session.IsRecording)
            {
                await _session.StopRecordingAsync();
            }
            else if (_session.State == BroadcastLegState.Playing)
            {
                var destination = await _session.StartRecordingAsync(_recordFolders(), _recordLog);
                _recordingFolder = destination?.Folder;
                if (destination is null && !_closed)
                {
                    Notice("RecordStartFailed");
                }
                else if (destination?.Skipped is { } skipped)
                {
                    Notice("RecordFellBack", skipped, destination.Folder);
                }
            }
        }
        finally
        {
            _recordBusy = false;
            if (!_closed) RefreshRecording();
        }
    });

    private void RecordTimer_Tick(object? sender, EventArgs e)
    {
        RefreshRecording();
        if (!_recordBusy && _session.IsRecording && !_session.IsRecordingWriting)
        {
            // Disk failure or a bounded queue overflow ends the copy without stopping playback.
            Record_Click(this, new RoutedEventArgs());
        }
    }

    private void RefreshRecording()
    {
        var recording = _session.IsRecording;
        RecordButton.IsEnabled = !_recordBusy && !_session.IsRecordingFinishing && _recordLog is not null &&
            (recording || _session.CanRecord);
        // S6-4: the button is disabled exactly when its tooltip (RecordUnsupportedFormat) explains why, and a disabled
        // control shows none unless asked to - the same call PlayerWindow.UpdateRecordingUi makes.
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(RecordButton, true);
        RecordButton.Style = (Style)FindResource(recording ? "StopRecordGlyphOnlyButton" : "RecordGlyphOnlyButton");
        RecordButton.SetResourceReference(ToolTipProperty, recording ? "StopRecordTip" :
            _session.State == BroadcastLegState.Playing && !_session.CanRecord ? "RecordUnsupportedFormat" : "RecordTip");
        RecordButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,
            recording ? "StopRecord" : "Record");
        RecordButton.Opacity = recording ? 1 : 0.6;
        if (_session.RecordingStartedAt is { } started)
        {
            RecordingText.Text = LocalizationService.Get("Recording") + " " + RecordingLength.Format(DateTimeOffset.Now - started);
        }
        else if (_session.IsRecordingFinishing)
        {
            RecordingText.Text = LocalizationService.Get("RecordSaving");
        }
    }

    private void RecordingCompleted(IReadOnlyList<SegmentResult> results, bool failed)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closed) RefreshRecording();
            foreach (var result in results)
            {
                if (failed || result.Fate == SegmentFate.Stranded)
                    Notice("RecordWriteFailed", result.Path ?? _recordingFolder ?? string.Empty);
                else if (result.Fate == SegmentFate.Saved)
                {
                    if (result.Skipped is { } skipped) Notice("RecordFellBack", skipped, Path.GetDirectoryName(result.Path));
                    Notice("RecordSavedWithLength", result.Path, RecordingLength.Format(result.Length));
                }
                else Notice("RecordNothingSaved");
            }
        });
    }

    private void Notice(string key, params object?[] arguments)
    {
        if (!_closed) RecordingText.Text = LocalizationService.Format(key, arguments);
        RecordingNotice?.Invoke(key, arguments);
    }
}
