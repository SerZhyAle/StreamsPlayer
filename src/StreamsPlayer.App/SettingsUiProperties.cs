using System.Windows;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0191: the stable internal ID a settings group is remembered by (<c>WINDOWS-UI</c> section 3.3).
/// Translated captions are never keys - a group keeps its context across a language change, and an ID
/// the current markup no longer has falls back to a valid page and anchor at restore.
/// </summary>
public static class SettingsUiProperties
{
    public static readonly DependencyProperty GroupIdProperty = DependencyProperty.RegisterAttached(
        "GroupId", typeof(string), typeof(SettingsUiProperties), new PropertyMetadata(default(string)));

    public static string? GetGroupId(DependencyObject obj) => (string?)obj.GetValue(GroupIdProperty);

    public static void SetGroupId(DependencyObject obj, string? value) => obj.SetValue(GroupIdProperty, value);

    /// <summary>
    /// SP-0234: a picture that leads a group's heading (the exchange mark beside "Exchange server",
    /// <c>EXCHANGE-MARK</c> rule 8a). It is a property of the group rather than part of its header so the header
    /// stays the text the toggle takes its accessible name from, and so every other group is drawn as before.
    /// </summary>
    public static readonly DependencyProperty HeaderMarkProperty = DependencyProperty.RegisterAttached(
        "HeaderMark", typeof(object), typeof(SettingsUiProperties), new PropertyMetadata(null));

    public static object? GetHeaderMark(DependencyObject obj) => obj.GetValue(HeaderMarkProperty);

    public static void SetHeaderMark(DependencyObject obj, object? value) => obj.SetValue(HeaderMarkProperty, value);
}
