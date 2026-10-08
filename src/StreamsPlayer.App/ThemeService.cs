using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>Resolves the saved choice to an application palette without leaking Windows APIs into Core.</summary>
public static partial class ThemeService
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
            ["DangerBrush"] = (Color.FromRgb(178, 34, 34), Color.FromRgb(255, 107, 107)),
            // SP-0191: the settings navigation's per-destination identity inks (ICON-RENDER 0.16
            // section 11, WINDOWS-UI 6.6). One table, light and dark values per destination, every
            // pair measured at 3:1 or better against every surface the nav row actually draws -
            // ControlBrush, ControlHoverBrush and CardSelectedBrush in both themes (gated in
            // IconographyConformanceTests alongside the ordinary glyph roles). Selection and the
            // accent bar stay palette roles; the ink identifies the destination, never the state.
            ["NavInkGeneral"] = (Color.FromRgb(35, 100, 170), Color.FromRgb(120, 189, 255)),
            ["NavInkLibrary"] = (Color.FromRgb(22, 110, 58), Color.FromRgb(96, 211, 126)),
            ["NavInkPlayback"] = (Color.FromRgb(168, 48, 59), Color.FromRgb(255, 118, 118)),
            ["NavInkAudio"] = (Color.FromRgb(122, 44, 160), Color.FromRgb(214, 143, 255)),
            ["NavInkFiles"] = (Color.FromRgb(150, 82, 18), Color.FromRgb(255, 190, 112)),
            ["NavInkAbout"] = (Color.FromRgb(90, 101, 122), Color.FromRgb(196, 208, 224))
        };

    private static AppTheme _preference = AppTheme.System;
    private static bool _listening;

    // SP-0132: what the palette currently holds. UserPreferenceChanged fires for wallpaper, power, locale and
    // a dozen other categories, and each used to re-create every palette brush and re-resolve every
    // DynamicResource in every window; only a change of this value is worth that. SP-0211 widens it from
    // "dark or light" to the whole resolved palette, so a switch of high-contrast theme (same mode, other
    // colours) is also a change and nothing else is.
    private static string? _appliedSignature;

    public static AppTheme Preference => _preference;

    /// <summary>True when the palette currently held is a dark one; false for light and for high contrast.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Raised on the UI thread after the palette brushes were replaced - a chosen theme, a system theme
    /// change or a high-contrast switch. A window that paints something outside the resource tree (the
    /// native title bar) re-applies it here.
    /// </summary>
    public static event Action? PaletteChanged;

    public static void Initialize()
    {
        Apply(AppTheme.System);
    }

    public static void Apply(AppTheme preference)
    {
        _preference = Enum.IsDefined(preference) ? preference : AppTheme.System;
        Subscribe();
        ApplyResolvedTheme();
    }

    public static void Shutdown()
    {
        if (!_listening)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _listening = false;
    }

    // SP-0211: high contrast is followed whatever the chosen mode (APP-SETTINGS rule 6, WINDOWS-UI 6.5), so
    // the subscription no longer depends on the preference being System.
    private static void Subscribe()
    {
        if (_listening)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        _listening = true;
    }

    private static void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility)
        {
            ReapplySoon();
        }
    }

    private static void SystemParameters_StaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast))
        {
            ReapplySoon();
        }
    }

    /// <summary>
    /// Background priority, so the framework's own cache of system colours and parameters has been
    /// invalidated by the same settings message before the palette reads it.
    /// </summary>
    private static void ReapplySoon()
    {
        if (Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyResolvedTheme);
    }

    private static void ApplyResolvedTheme()
    {
        var highContrast = SystemParameters.HighContrast;
        var isDark = !highContrast && _preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !SystemUsesLightTheme()
        };

        var system = highContrast ? HighContrastColours() : null;
        var signature = system is null
            ? (isDark ? "dark" : "light")
            : "contrast:" + string.Join(',', system.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value));
        if (_appliedSignature == signature)
        {
            return;
        }

        _appliedSignature = signature;
        IsDark = isDark;
        var resources = Application.Current.Resources;
        foreach (var (key, colors) in Palette)
        {
            var color = system is not null && system.TryGetValue(key, out var systemColor)
                ? systemColor
                : isDark ? colors.Dark : colors.Light;
            resources[key] = new SolidColorBrush(color);
        }

        PaletteChanged?.Invoke();
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(AppsUseLightThemeValue) is not int value || value != 0;
    }
}
