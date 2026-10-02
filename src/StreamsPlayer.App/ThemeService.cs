using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>Resolves the saved choice to an application palette without leaking Windows APIs into Core.</summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly IReadOnlyDictionary<string, (Color Light, Color Dark)> Palette =
        new Dictionary<string, (Color Light, Color Dark)>
        {
            ["WindowBackground"] = (Color.FromRgb(245, 247, 250), Color.FromRgb(16, 23, 34)),
            ["SurfaceBrush"] = (Colors.White, Color.FromRgb(23, 34, 54)),
            ["TextBrush"] = (Color.FromRgb(23, 34, 54), Color.FromRgb(245, 247, 250)),
            ["MutedBrush"] = (Color.FromRgb(99, 112, 131), Color.FromRgb(184, 200, 219)),
            ["AccentBrush"] = (Color.FromRgb(35, 100, 170), Color.FromRgb(31, 78, 138)),
            ["AccentTextBrush"] = (Colors.White, Colors.White),
            // SP-0162: the accent as a mark drawn on a themed surface (menu check, selected-tab stripe,
            // selection and filter borders, the keyboard focus ring). AccentBrush is a fill that carries
            // AccentTextBrush on top, so in the dark theme it stays dark enough for white text and reads
            // about 1.4-1.7:1 against the control colours; the dark mark is the light end of the same hue,
            // 5.8:1 against the hover row, the weakest pairing. SelectionBrush is the text selection's plate,
            // which keeps white text on it (4.7:1) and stands 3.1:1 off the text box.
            ["AccentMarkBrush"] = (Color.FromRgb(35, 100, 170), Color.FromRgb(120, 189, 255)),
            ["SelectionBrush"] = (Color.FromRgb(35, 100, 170), Color.FromRgb(56, 116, 200)),
            ["BorderBrush"] = (Color.FromRgb(213, 220, 230), Color.FromRgb(51, 65, 85)),
            ["ControlBrush"] = (Colors.White, Color.FromRgb(31, 42, 58)),
            ["ControlHoverBrush"] = (Color.FromRgb(239, 246, 255), Color.FromRgb(42, 58, 82)),
            ["ControlPressedBrush"] = (Color.FromRgb(212, 226, 242), Color.FromRgb(62, 81, 112)),
            ["CardBrush"] = (Color.FromRgb(248, 250, 252), Color.FromRgb(26, 37, 53)),
            ["CardSelectedBrush"] = (Color.FromRgb(232, 241, 250), Color.FromRgb(33, 59, 88)),
            ["SectionBrush"] = (Color.FromRgb(238, 242, 247), Color.FromRgb(35, 48, 68)),
            ["FaviconPlateBrush"] = (Color.FromRgb(240, 242, 245), Color.FromRgb(240, 242, 245)),
            ["DisabledTextBrush"] = (Color.FromRgb(138, 150, 168), Color.FromRgb(116, 129, 151)),
            ["InfoBrush"] = (Color.FromRgb(56, 108, 141), Color.FromRgb(134, 197, 244)),
            ["GeoHintBrush"] = (Color.FromArgb(34, 179, 107, 0), Color.FromArgb(51, 213, 128, 0)),
            ["GeoTextBrush"] = (Color.FromRgb(138, 83, 0), Color.FromRgb(255, 213, 128)),
            ["LinkBrush"] = (Color.FromRgb(0, 103, 192), Color.FromRgb(120, 189, 255)),
            // SP-0114: the APP-STYLE roles success, warning and danger - a state mark on a themed surface
            // (a channel's last play outcome, the stop-recording glyph). Each pair is darker on the light
            // surfaces and lighter on the dark ones, so the mark keeps its contrast in both themes.
            ["SuccessBrush"] = (Color.FromRgb(30, 126, 52), Color.FromRgb(86, 196, 110)),
            ["WarningBrush"] = (Color.FromRgb(239, 108, 0), Color.FromRgb(230, 180, 60)),
            ["DangerBrush"] = (Color.FromRgb(178, 34, 34), Color.FromRgb(255, 107, 107))
        };

    private static AppTheme _preference = AppTheme.System;
    private static bool _listening;

    // SP-0132: what the palette currently holds. UserPreferenceChanged fires for wallpaper, power, locale and
    // a dozen other categories, and each used to re-create every palette brush and re-resolve every
    // DynamicResource in every window; only a change of this value is worth that.
    private static bool? _appliedDark;

    public static AppTheme Preference => _preference;

    public static void Initialize()
    {
        Apply(AppTheme.System);
    }

    public static void Apply(AppTheme preference)
    {
        _preference = Enum.IsDefined(preference) ? preference : AppTheme.System;
        UpdateSystemThemeSubscription();
        ApplyResolvedTheme();
    }

    public static void Shutdown()
    {
        if (!_listening)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _listening = false;
    }

    private static void UpdateSystemThemeSubscription()
    {
        if (_preference == AppTheme.System && !_listening)
        {
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            _listening = true;
        }
        else if (_preference != AppTheme.System && _listening)
        {
            Shutdown();
        }
    }

    private static void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference != AppTheme.System || Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, ApplyResolvedTheme);
    }

    private static void ApplyResolvedTheme()
    {
        var isDark = _preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !SystemUsesLightTheme()
        };

        if (_appliedDark == isDark)
        {
            return;
        }

        _appliedDark = isDark;
        var resources = Application.Current.Resources;
        foreach (var (key, colors) in Palette)
        {
            resources[key] = new SolidColorBrush(isDark ? colors.Dark : colors.Light);
        }
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(AppsUseLightThemeValue) is not int value || value != 0;
    }
}
