using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0230: the application side of CONTEXT-HELP - one command binding on every window, one launcher.
/// </summary>
/// <remarks>
/// F1 is the gesture <see cref="ApplicationCommands.Help"/> already carries, and the help button of each window is a
/// button bound to that same command, so the key and the button cannot reach different targets (rule 3). The binding is
/// registered once for the <see cref="Window"/> class: no window holds help code, and no key is registered
/// system-wide (rule 1) - the command is routed only while one of this application's windows has the focus.
/// Pages open in the system browser from the copy beside the executable and never touch the network (rule 12).
/// </remarks>
internal static class HelpService
{
    private static readonly Lazy<HelpMap> Map = new(HelpMap.Load);

    /// <summary>Called once from <c>App.OnStartup</c>, before the first window is created.</summary>
    internal static void Register() =>
        CommandManager.RegisterClassCommandBinding(
            typeof(Window), new CommandBinding(ApplicationCommands.Help, Executed, CanExecute));

    private static void CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        // A window with no help of its own leaves F1 a no-op and its button, if it were given one, disabled;
        // handled either way, so the key never falls through to a control that might read it differently.
        e.CanExecute = sender is Window window && Resolve(window, null) is not null;
        e.Handled = true;
    }

    private static void Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        e.Handled = true;
        Open(window, e.OriginalSource as DependencyObject);
    }

    /// <summary>
    /// The target for a window: the anchor of the focused element if it has one, else the window's own context
    /// (the Settings page on screen), else the top of the window's help.
    /// </summary>
    internal static HelpTarget? Resolve(Window window, DependencyObject? from)
    {
        var surface = Map.Value.FindByClass(window.GetType().Name);
        if (surface is null)
        {
            return null;
        }

        var anchor = FindAnchor(from, window) ?? (window as IHelpContext)?.HelpSectionAnchor;
        return HelpResolver.Resolve(Map.Value, surface.Id, anchor);
    }

    /// <summary>
    /// The anchor of one page of a window whose help is a section per page, taken from the map so the order of the
    /// pages is declared once. Null for an index the map does not hold, which opens the top of the help.
    /// </summary>
    internal static string? PageAnchor(string surfaceId, int pageIndex)
    {
        var pages = Map.Value.Find(surfaceId)?.Pages;
        return pages is not null && pageIndex >= 0 && pageIndex < pages.Count ? pages[pageIndex].Anchor : null;
    }

    internal static void Open(Window owner, DependencyObject? from)
    {
        var target = Resolve(owner, from);
        var root = Path.Combine(AppContext.BaseDirectory, HelpPageLocator.FolderName);
        var language = InterfaceLanguages.For(LocalizationService.CurrentLanguage).DictionaryCode;
        var relative = HelpPageLocator.Locate(
            Map.Value, target, language, page => File.Exists(Path.Combine(root, page.Replace('/', Path.DirectorySeparatorChar))));

        if (relative is null)
        {
            // A build that shipped without its help: say so, and open nothing - the product never reaches for the
            // network to make up for it.
            MessageBox.Show(owner, LocalizationService.Get("HelpUnavailable"), LocalizationService.Get("HelpName"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // The page follows the application's own theme when the user chose one; with "system" it follows the system's,
        // which is what the page does by itself without a query (CONTEXT-HELP rule 10).
        var address = HelpPageLocator.FileAddress(
            root, relative, ThemeService.Preference switch { AppTheme.Light => "theme=light", AppTheme.Dark => "theme=dark", _ => null });

        try
        {
            var how = BrowserLauncher.Open(address);
            HandlerBoundary.Record($"Help opened: surface={target?.SurfaceId ?? "(index)"} page={relative} via={how}");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, LocalizationService.Get("SettingsOpenLinkFailed"),
                LocalizationService.Get("SettingsOpenLinkFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? FindAnchor(DependencyObject? start, Window window)
    {
        for (var current = start; current is not null && !ReferenceEquals(current, window); current = ParentOf(current))
        {
            if (HelpAnchor.GetId(current) is { Length: > 0 } id)
            {
                return id;
            }
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
