using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>What one card shows, already in words: the window decides, the card only draws (SP-0252).</summary>
internal sealed record BroadcastCardView(
    string Title,
    BroadcastSourcePhase Phase,
    string StateText,
    string StartCaption,
    bool CanStart,
    string? Note,
    IReadOnlyList<string> Where,
    bool CanShowRecipients);

/// <summary>
/// One source of the broadcast window: its state, the choices that belong to it, and Start, Cancel, Stop, Share and Cast.
/// The card holds no producer and no settings; it raises what the person asked for and the window answers.
/// </summary>
public partial class BroadcastSourceCard : UserControl
{
    public BroadcastSourceCard()
    {
        InitializeComponent();
    }

    /// <summary>Which source this card is; set once by the window.</summary>
    internal BroadcastSource Source { get; set; }

    internal event EventHandler? StartRequested;

    internal event EventHandler? CancelRequested;

    internal event EventHandler? StopRequested;

    internal event EventHandler? ShareRequested;

    internal event EventHandler? CastRequested;

    internal event EventHandler? RecipientsRequested;

    internal ComboBox FirstChoice => FirstChoiceBox;

    internal ComboBox SecondChoice => SecondChoiceBox;

    internal Button ShareAnchor => ShareButton;

    internal Button CastAnchor => CastButton;

    internal Button StartAnchor => StartButton;

    /// <summary>
    /// Names the choices this source has. A null label hides that choice; both null hides the grid. The window fills the
    /// items and listens to the selection, so the card needs no knowledge of devices.
    /// </summary>
    internal void SetChoices(string? firstLabel, string? secondLabel)
    {
        FirstChoiceLabel.Text = firstLabel ?? string.Empty;
        FirstChoiceLabel.Visibility = FirstChoiceBox.Visibility = firstLabel is null ? Visibility.Collapsed : Visibility.Visible;
        SecondChoiceLabel.Text = secondLabel ?? string.Empty;
        SecondChoiceLabel.Visibility = SecondChoiceBox.Visibility = secondLabel is null ? Visibility.Collapsed : Visibility.Visible;
        ChoicesGrid.Visibility = firstLabel is null && secondLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A choice that cannot change while its source is on the air (it is read at the start).</summary>
    internal void SetChoicesEnabled(bool enabled) => FirstChoiceBox.IsEnabled = SecondChoiceBox.IsEnabled = enabled;

    internal void Show(BroadcastCardView view)
    {
        TitleText.Text = view.Title;
        StateText.Text = view.StateText;
        var live = view.Phase == BroadcastSourcePhase.Live;
        var starting = view.Phase == BroadcastSourcePhase.Starting;
        var stopping = view.Phase == BroadcastSourcePhase.Stopping;

        // The dot is shape, the words are text, the colour is the third channel: a filled dot is on the air.
        if (live)
        {
            StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DangerBrush");
        }
        else
        {
            StateDot.Fill = System.Windows.Media.Brushes.Transparent;
        }

        StateDot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, view.Phase switch
        {
            BroadcastSourcePhase.Live or BroadcastSourcePhase.Failed => "DangerBrush",
            BroadcastSourcePhase.Starting or BroadcastSourcePhase.Stopping => "AccentBrush",
            _ => "MutedBrush"
        });
        StateText.SetResourceReference(TextBlock.ForegroundProperty,
            view.Phase == BroadcastSourcePhase.Failed ? "DangerBrush" : live ? "TextBrush" : "MutedBrush");

        StartButton.Content = view.StartCaption;
        StartButton.Visibility = live || starting || stopping ? Visibility.Collapsed : Visibility.Visible;
        StartButton.IsEnabled = view.CanStart;
        StartProgress.Visibility = starting || stopping ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = starting ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(CancelButton, LocalizationService.Format("BroadcastSourceDevice", view.Title, LocalizationService.Get("Cancel")));
        StopButton.Visibility = live || stopping ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = live;
        AutomationProperties.SetName(StopButton, LocalizationService.Format("BroadcastStopSource", view.Title));

        // Share and cast belong to a source that is on the air; while it is being stopped there is nothing to hand out.
        ShareButton.Visibility = CastButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ShareButton, LocalizationService.Format("BroadcastSourceDevice", view.Title, LocalizationService.Get("BroadcastShareMenu")));
        AutomationProperties.SetName(CastButton, LocalizationService.Format("BroadcastSourceDevice", view.Title, LocalizationService.Get("BroadcastCastMenu")));
        RecipientsButton.Visibility = live && view.CanShowRecipients ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(RecipientsButton, LocalizationService.Format("BroadcastSourceDevice", view.Title, LocalizationService.Get("BroadcastShowRecipients")));

        NoteText.Text = view.Note ?? string.Empty;
        NoteText.Visibility = string.IsNullOrEmpty(view.Note) ? Visibility.Collapsed : Visibility.Visible;
        WhereText.Text = string.Join(Environment.NewLine, view.Where);
        WhereText.Visibility = live && view.Where.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetChoicesEnabled(!live && !starting && !stopping);
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);

    private void Share_Click(object sender, RoutedEventArgs e) => ShareRequested?.Invoke(this, EventArgs.Empty);

    private void Cast_Click(object sender, RoutedEventArgs e) => CastRequested?.Invoke(this, EventArgs.Empty);

    private void Recipients_Click(object sender, RoutedEventArgs e) => RecipientsRequested?.Invoke(this, EventArgs.Empty);
}
