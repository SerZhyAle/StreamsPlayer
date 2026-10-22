namespace StreamsPlayer.App;

// SP-0230: F1 and the help button open the section of the help page that matches the settings page on screen.
// The anchors and their order are the help map's; the gate holds that order against PageNameKeys.
public partial class SettingsWindow : IHelpContext
{
    public string? HelpSectionAnchor => HelpService.PageAnchor("settings", _currentPageIndex);
}
