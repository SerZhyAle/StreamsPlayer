using System.Windows;

using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>One row of the interface-language list: an endonym, and whether it is the language currently in use.</summary>
/// <param name="Language">The value written to state when this row is confirmed.</param>
/// <param name="Endonym">The language's name in its own language and script.</param>
/// <param name="IsActive">Whether this is the language the interface is currently showing.</param>
internal sealed record LanguageChoice(AppLanguage Language, string Endonym, bool IsActive)
{
    // Public on purpose: WPF binds through reflection over public members only, so an internal
    // property silently resolves to nothing and the marker keeps its default Visible - a check mark
    // on every row. The record itself stays internal.
    public Visibility ActiveMarkerVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
}
