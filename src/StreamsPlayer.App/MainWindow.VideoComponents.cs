using System.IO;
using System.Net.Http;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0026: installs and removes the FFmpeg natives the opt-in FlyleafLib engine binds against. They
/// are not shipped in any package - the natives published alongside FlyleafLib are GPLv3, and the set
/// is ~143 MB - so they are fetched from an LGPL-3.0 build on an explicit user request. Nothing here
/// runs on startup or in the background; the only call sites are the two buttons in the Tools window.
/// </summary>
public partial class MainWindow
{
    private async Task InstallVideoComponentsAsync(Window owner)
    {
        var settings = owner as SettingsWindow;
        var target = FFmpegComponents.ResolveFolder(_dataDirectory);

        // SP-0128: FFmpeg's libraries stay mapped until the process exits, so a set the engine already
        // loaded cannot be swapped under it; the new one would only half-replace it.
        if (FlyleafVideoBackend.LoadedFFmpegPath is { } loaded
            && string.Equals(Path.GetFullPath(loaded).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            _log.Event("FFMPEG INSTALL", "ok=false", "err=InUse", $"folder={target}");
            MessageBox.Show(owner, LocalizationService.Get("VideoComponentsInUse"),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                owner,
                LocalizationService.Format(
                    "VideoComponentsConfirm",
                    FFmpegComponentsInstaller.ApproximateDownloadMegabytes,
                    FFmpegComponentsInstaller.ApproximateInstalledMegabytes,
                    FFmpegComponentsInstaller.SourceDescription),
                LocalizationService.Get("VideoComponentsTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        // SP-0128: the catalog client, whose requests carry explicit bounds rather than a client timeout.
        // The installer bounds the head and the body's silence itself, and the user can cancel.
        var installer = new FFmpegComponentsInstaller(_catalogHttpClient);
        var progress = new Progress<FFmpegInstallProgress>(report => settings?.ShowInstallProgress(report));
        var cancellation = settings?.BeginVideoComponentsInstall() ?? CancellationToken.None;
        _log.Event("FFMPEG INSTALL", "action=start", $"url={FFmpegComponentsInstaller.SourceUrl}");
        try
        {
            var folder = await installer.InstallAsync(_dataDirectory, progress, cancellation);
            _log.Event("FFMPEG INSTALL", "ok=true", $"folder={folder}");
            MessageBox.Show(owner, LocalizationService.Format("VideoComponentsInstallDone", folder),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The user's own choice; the status line already restates what is installed.
            _log.Event("FFMPEG INSTALL", "ok=false", "err=Cancelled");
        }
        catch (FFmpegComponentsRollbackException exception)
        {
            // SP-0178: the only place the preserved folder is ever named to the user. The components folder
            // is marked incomplete, so no later install sweeps the preserved copy before a set is in place.
            _log.Event("FFMPEG INSTALL", "ok=false", "err=RollbackFailed",
                $"preserved={exception.PreservedFolder}", $"msg={exception.InnerException?.Message}");
            MessageBox.Show(owner, LocalizationService.Format("VideoComponentsRollbackFailed", exception.PreservedFolder),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
                                              or UnauthorizedAccessException or TimeoutException
                                              or TaskCanceledException or FFmpegArchiveMismatchException)
        {
            _log.Event("FFMPEG INSTALL", "ok=false", $"err={exception.GetType().Name}", $"msg={exception.Message}");
            var cause = exception is FFmpegArchiveMismatchException
                ? LocalizationService.Get("VideoComponentsVerifyFailed")
                : FailureCauseText.Describe(exception);
            MessageBox.Show(owner, LocalizationService.Format("VideoComponentsInstallFailed", cause),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            settings?.EndVideoComponentsInstall();
        }
    }

    private Task RemoveVideoComponentsAsync(Window owner)
    {
        if (MessageBox.Show(owner, LocalizationService.Get("VideoComponentsRemoveConfirm"),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
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
            // The message already names that cause and its action, so the exception goes to the log only.
            _log.Event("FFMPEG REMOVE", "ok=false", $"err={exception.GetType().Name}", $"msg={exception.Message}");
            MessageBox.Show(owner, LocalizationService.Get("VideoComponentsRemoveFailed"),
                LocalizationService.Get("VideoComponentsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }
}
