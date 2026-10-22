using System.IO;
using System.Windows;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0231 (<c>UI-SIZING</c> 1.1): holds the interface profile the user chose and publishes the measurements
/// every covered component shares as application resources.
/// </summary>
/// <remarks>
/// The numbers are decided once, in <see cref="UiMetricsResolver"/>; this class only moves them into the resource
/// dictionary under the <c>Ui*</c> keys <c>App.xaml</c> declares defaults for, so a component names a token and a
/// <c>DynamicResource</c> follows a change. The profile is read once when the process starts and changes only
/// through <see cref="Apply"/> - an explicit user choice - never from an input event or a window event
/// (<c>UI-SIZING</c> section 2 rule 1). Font size is published too, because it reaches popups, drop-down lists,
/// menus and tooltips by property inheritance, where a layout transform on a window would not (plan decision V6).
/// </remarks>
public static class UiProfileService
{
    private static UiProfileStore? _store;

    public static UiProfile Current { get; private set; } = UiProfile.Default;

    public static UiMetrics Metrics { get; private set; } = UiMetricsResolver.Resolve(UiProfile.Default);

    /// <summary>
    /// Raised on the UI thread after the resources were replaced. A window that sizes itself from the profile
    /// (the settings window refits to the work area) re-applies there; everything bound with a
    /// <c>DynamicResource</c> has already followed.
    /// </summary>
    public static event Action? Changed;

    public static void Initialize(string dataDirectory)
    {
        _store = new UiProfileStore(dataDirectory);
        Set(_store.LoadSync());
    }

    /// <summary>
    /// Makes <paramref name="profile"/> the interface profile, publishes it and remembers it. The write is
    /// fire-and-forget on purpose: a profile that could not be saved still holds for this session, and the cost of
    /// the failure is one forgotten choice, not a broken window.
    /// </summary>
    public static void Apply(UiProfile profile)
    {
        if (profile == Current)
        {
            return;
        }

        Set(profile);
        if (_store is { } store)
        {
            _ = SaveSafelyAsync(store, profile);
        }
    }

    private static async Task SaveSafelyAsync(UiProfileStore store, UiProfile profile)
    {
        try
        {
            if (!await store.SaveAsync(profile).ConfigureAwait(false))
            {
                HandlerBoundary.Report(nameof(UiProfileService), new IOException("ui-profile.json was not written."), notifyUser: false);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(UiProfileService), exception, notifyUser: false);
        }
    }

    private static void Set(UiProfile profile)
    {
        Current = profile;
        Metrics = UiMetricsResolver.Resolve(profile);
        if (Application.Current is { } application)
        {
            Publish(application.Resources, Metrics);
        }

        Changed?.Invoke();
    }

    /// <summary>The keys <see cref="Publish"/> writes - the set <c>App.xaml</c> must declare defaults for.</summary>
    internal static readonly string[] PublishedKeys =
    [
        "UiScale", "UiFontSize", "UiFontProduct", "UiFontTitle", "UiFontHeading", "UiTargetHeight", "UiButtonPaddingX", "UiGlyphSize", "UiGlyphGap", "UiCornerRadius",
        "UiBorderWidth", "UiButtonGap", "UiSemanticGroupGap", "UiCheckBoxBox", "UiMarkerSize", "UiNavRowHeight",
        "UiNavGlyphSize", "UiNavIndent", "UiBorderThickness", "UiOptionCellMargin", "UiOptionHeadingMargin", "UiInlineGapAfter",
        "UiInlineGapBefore", "UiMarkerMargin", "UiGroupBodyMargin", "UiSiblingGroupMargin"
    ];

    internal static void Publish(ResourceDictionary resources, UiMetrics metrics)
    {
        resources["UiScale"] = metrics.Scale;
        resources["UiFontSize"] = metrics.FontSize;
        resources["UiFontProduct"] = metrics.ProductFontSize;
        resources["UiFontTitle"] = metrics.TitleFontSize;
        resources["UiFontHeading"] = metrics.HeadingFontSize;
        resources["UiTargetHeight"] = metrics.TargetHeight;
        resources["UiButtonPaddingX"] = metrics.ButtonPaddingX;
        resources["UiGlyphSize"] = metrics.GlyphSize;
        resources["UiGlyphGap"] = metrics.GlyphGap;
        resources["UiCornerRadius"] = new CornerRadius(metrics.CornerRadius);
        resources["UiBorderWidth"] = metrics.BorderWidth;
        resources["UiButtonGap"] = metrics.ButtonGap;
        resources["UiSemanticGroupGap"] = metrics.SemanticGroupGap;
        resources["UiCheckBoxBox"] = metrics.CheckBoxBox;
        resources["UiMarkerSize"] = metrics.MarkerSize;
        resources["UiNavRowHeight"] = metrics.NavRowHeight;
        resources["UiNavGlyphSize"] = metrics.NavGlyphSize;
        resources["UiNavIndent"] = metrics.NavIndent;
        resources["UiBorderThickness"] = new Thickness(metrics.BorderWidth);
        resources["UiOptionCellMargin"] = new Thickness(metrics.OptionCellGap, 0, 0, 0);
        resources["UiOptionHeadingMargin"] = new Thickness(0, metrics.OptionHeadingGap, 0, 0);
        resources["UiInlineGapAfter"] = new Thickness(0, 0, metrics.OptionInlineGap, 0);
        resources["UiInlineGapBefore"] = new Thickness(metrics.OptionInlineGap, 0, 0, 0);
        resources["UiMarkerMargin"] = new Thickness(0, 0, metrics.MarkerGap, 0);
        resources["UiGroupBodyMargin"] = new Thickness(metrics.GroupBodyInset, metrics.SiblingGroupGap, 0, 2);
        resources["UiSiblingGroupMargin"] = new Thickness(0, 0, 0, metrics.SiblingGroupGap);
    }
}
