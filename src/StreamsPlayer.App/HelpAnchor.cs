using System.Windows;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0230: names the help entry an element has, so F1 pressed while it has the focus opens the page at that entry
/// (CONTEXT-HELP rule 3). The value is the anchor the generated page carries - <c>e-search</c> for the entry whose id
/// in the copy file is <c>search</c> - and is checked against the pages by the gate, so a typo is a failing test and
/// not a page that opens at its top.
/// </summary>
public static class HelpAnchor
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(HelpAnchor), new PropertyMetadata(null));

    public static string? GetId(DependencyObject element) => (string?)element.GetValue(IdProperty);

    public static void SetId(DependencyObject element, string? value) => element.SetValue(IdProperty, value);
}

/// <summary>
/// SP-0230: a window whose help depends on what it is showing says where its help opens. Settings is the one that
/// does today: its page on screen decides the section. The answer is consulted only when no focused element has an
/// anchor of its own, so a key press inside a page and a click on the help button reach the same section.
/// </summary>
public interface IHelpContext
{
    string? HelpSectionAnchor { get; }
}
