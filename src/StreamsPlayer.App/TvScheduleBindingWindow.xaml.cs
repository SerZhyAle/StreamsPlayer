using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

internal enum TvScheduleBindingChoice
{
    Automatic,
    None,
    Channel
}

/// <summary>
/// SP-0075: the user's own answer to "which schedule channel is this?" - the fix for a missed or wrong
/// name match. Only collects the answer; the owner stores it.
/// </summary>
public partial class TvScheduleBindingWindow : Window
{
    private const int MaximumListed = 500;

    private readonly IReadOnlyList<Option> _options;

    internal TvScheduleBindingWindow(string channelTitle, IReadOnlyList<TvScheduleChannel> channels, string? currentId)
    {
        InitializeComponent();
        HintText.Text = LocalizationService.Format("TvScheduleBindingHint", channelTitle);
        _options = channels
            .Select(channel => new Option(channel.Id, channel.Names.Count > 0
                ? $"{channel.Names[0]}  ·  {channel.Id}"
                : channel.Id))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Filter(string.Empty);
        if (currentId is not null && _options.FirstOrDefault(option => option.Id == currentId) is { } current)
        {
            ChannelList.SelectedItem = current;
            ChannelList.ScrollIntoView(current);
        }

        Loaded += (_, _) => SearchBox.Focus();
    }

    internal TvScheduleBindingChoice Choice { get; private set; } = TvScheduleBindingChoice.Automatic;

    /// <summary>The chosen schedule channel; null for Automatic and for No schedule.</summary>
    internal string? SelectedChannelId { get; private set; }

    private void Filter(string text)
    {
        var term = text.Trim();
        // Capped: a national guide lists thousands of channels, and a list that long is searched, not scrolled.
        ChannelList.ItemsSource = (term.Length == 0
                ? _options
                : _options.Where(option => option.Label.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            .Take(MaximumListed)
            .ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Filter(SearchBox.Text);

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BindButton.IsEnabled = ChannelList.SelectedItem is Option;

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChannelList.SelectedItem is Option)
        {
            Bind_Click(sender, e);
        }
    }

    private void Automatic_Click(object sender, RoutedEventArgs e) => Commit(TvScheduleBindingChoice.Automatic, null);

    private void None_Click(object sender, RoutedEventArgs e) => Commit(TvScheduleBindingChoice.None, null);

    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is Option option)
        {
            Commit(TvScheduleBindingChoice.Channel, option.Id);
        }
    }

    private void Commit(TvScheduleBindingChoice choice, string? id)
    {
        Choice = choice;
        SelectedChannelId = id;
        DialogResult = true;
    }

    // Public: WPF binds DisplayMemberPath by reflection and cannot read a non-public type.
    public sealed record Option(string Id, string Label);
}
