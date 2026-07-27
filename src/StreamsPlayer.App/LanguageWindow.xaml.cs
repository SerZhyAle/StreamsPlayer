using System.Windows;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0034 decision 6: the interface-language picker.
/// <para>
/// Replaces a flat checkable menu behind a two-letter badge, which did not scale to thirteen entries
/// and was already ambiguous - the Ukrainian ISO code <c>uk</c> reads as "United Kingdom". Every row is
/// named by its own endonym so the list is reachable without knowing any of the other twelve languages.
/// </para>
/// </summary>
public partial class LanguageWindow : Window
{
    /// <summary>One row: an endonym, and whether it is the language currently in use.</summary>
    /// <param name="Language">The value written to state when this row is confirmed.</param>
    /// <param name="Endonym">The language's name in its own language and script.</param>
    /// <param name="IsActive">Whether this is the language the interface is currently showing.</param>
    internal sealed record LanguageChoice(AppLanguage Language, string Endonym, bool IsActive)
    {
        internal Visibility ActiveMarkerVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    internal LanguageWindow(AppLanguage current)
    {
        InitializeComponent();

        var choices = InterfaceLanguages.All
            .Select(entry => new LanguageChoice(
                entry.Language,
                LocalizationService.NativeName(entry.Language),
                entry.Language == current))
            .ToArray();

        LanguageList.ItemsSource = choices;
        LanguageList.SelectedItem = choices.FirstOrDefault(choice => choice.IsActive) ?? choices[0];
        // Focus the list rather than the button, so the arrow keys work without a Tab first.
        Loaded += (_, _) =>
        {
            LanguageList.Focus();
            LanguageList.ScrollIntoView(LanguageList.SelectedItem);
        };
    }

    /// <summary>The language the user confirmed, or <c>null</c> when the dialog was cancelled.</summary>
    internal AppLanguage? SelectedLanguage { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Commit();

    private void LanguageList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void LanguageList_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter commits from the list itself. IsDefault on the button would also fire, but only while
        // the button has focus somewhere in the chain; this makes the list's own Enter explicit.
        if (e.Key is Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
    }

    private void Commit()
    {
        if (LanguageList.SelectedItem is not LanguageChoice choice)
        {
            return;
        }

        // Confirming the active language is not a change; report nothing so the caller does not rewrite
        // state or flicker the interface.
        SelectedLanguage = choice.IsActive ? null : choice.Language;
        DialogResult = true;
    }
}
