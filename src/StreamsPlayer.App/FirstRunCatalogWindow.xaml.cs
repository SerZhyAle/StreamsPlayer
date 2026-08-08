using System.Windows;

namespace StreamsPlayer.App;

/// <summary>The three answers to SP-0059's first-launch question.</summary>
public enum FirstRunCatalogChoice
{
    /// <summary>Nothing is downloaded and the list stays empty. Also what a dismissal means.</summary>
    Decline,

    /// <summary>Fetch the shared channel list from the internet.</summary>
    Internet,

    /// <summary>Use the copy that shipped inside this build, with no network request.</summary>
    Bundled
}

/// <summary>
/// SP-0059: the one question a clean install asks - where the channel list should come from. It is an
/// offer, never an action: nothing here opens a connection, reads the bundled snapshot, or writes
/// state. The window returns an answer and closes; every consequence belongs to the caller.
/// </summary>
public partial class FirstRunCatalogWindow : Window
{
    /// <summary>
    /// Defaults to the harmless answer, which is what makes dismissal free without a single extra
    /// branch: Escape, the title-bar close button and Alt+F4 all leave this untouched.
    /// </summary>
    public FirstRunCatalogChoice Choice { get; private set; } = FirstRunCatalogChoice.Decline;

    /// <param name="bundledSnapshotAvailable">
    /// Whether this build carries a built-in copy. Passed in rather than read from Core here, so the
    /// window stays a pure question and the no-copy layout is reachable without a build that lacks one.
    /// </param>
    public FirstRunCatalogWindow(bool bundledSnapshotAvailable)
    {
        InitializeComponent();
        if (!bundledSnapshotAvailable)
        {
            BundledButton.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => InternetButton.Focus();
    }

    private void Internet_Click(object sender, RoutedEventArgs e) => Answer(FirstRunCatalogChoice.Internet);

    private void Bundled_Click(object sender, RoutedEventArgs e) => Answer(FirstRunCatalogChoice.Bundled);

    private void Answer(FirstRunCatalogChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
