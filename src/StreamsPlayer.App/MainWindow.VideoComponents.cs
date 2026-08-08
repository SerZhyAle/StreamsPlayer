using System.IO;
using System.Net.Http;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026: installs and removes the FFmpeg natives the opt-in FlyleafLib engine binds against. They
/// are not shipped in any package - the natives published alongside FlyleafLib are GPLv3, and the set
/// is ~143 MB - so they are fetched from an LGPL-3.0 build on an explicit user request. Nothing here
/// runs on startup or in the background; the only call sites are the two Settings buttons.
/// </summary>
public partial class MainWindow
{
    private async Task InstallVideoComponentsAsync(Window owner)
    {
        var settings = owner as SettingsWindow;
        if (MessageBox.Show(
                owner,
                LocalizationService.Format(
                    "VideoComponentsConfirm",
                    FFmpegComponentsInstaller.ApproximateDownloadMegabytes,
                    FFmpegComponentsInstaller.ApproximateInstalledMegabytes,
                    FFmpegComponentsInstaller.SourceDescription),
                LocalizationService.Get("VideoComponentsTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var installer = new FFmpegComponentsInstaller(_httpClient);
        var progress = new Progress<FFmpegInstallProgress>(report => settings?.ShowInstallProgress(report));
        settings?.SetVideoComponentsBusy(true);
        try
        {
            var folder = await installer.InstallAsync(_dataDirectory, progress);
            _log.Event("FFMPEG INSTALL", "ok=true", $"folder={folder}");
            MessageBox.Show(owner, LocalizationService.Format("VideoComponentsInstallDone", folder),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
                                              or UnauthorizedAccessException or TaskCanceledException)
        {
            _log.Event("FFMPEG INSTALL", "ok=false", $"err={exception.GetType().Name}", $"msg={exception.Message}");
            MessageBox.Show(owner, LocalizationService.Format("VideoComponentsInstallFailed", exception.Message),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            settings?.SetVideoComponentsBusy(false);
        }
    }

    private Task RemoveVideoComponentsAsync(Window owner)
    {
        if (MessageBox.Show(owner, LocalizationService.Get("VideoComponentsRemoveConfirm"),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return Task.CompletedTask;
        }

        try
        {
            FFmpegComponents.Remove(FFmpegComponents.ResolveFolder(_dataDirectory));
            _log.Event("FFMPEG REMOVE", "ok=true");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Most often a library still mapped by a player window opened on FlyleafLib this session.
            _log.Event("FFMPEG REMOVE", "ok=false", $"err={exception.GetType().Name}");
            MessageBox.Show(owner, LocalizationService.Format("VideoComponentsRemoveFailed", exception.Message),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }
}
