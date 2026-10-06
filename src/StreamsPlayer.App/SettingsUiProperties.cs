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
}
