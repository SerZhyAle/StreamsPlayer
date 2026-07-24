using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>One row of the manage-collections list; only the name is editable in place.</summary>
internal sealed class CollectionRowView : INotifyPropertyChanged
{
    private string _name;

    internal CollectionRowView(ChannelCollection collection)
    {
        Id = collection.Id;
        _name = collection.Name;
        Count = collection.ChannelIds.Count;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Guid Id { get; }
    internal int Count { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public string CountText => LocalizationService.Format("CollectionCount", Count);
}

// SP-0017: create, rename and delete collections. Deleting a collection never deletes a channel, so the
// confirmation says exactly that; renaming is rejected (and reverted) when the name is blank or taken.
public partial class CollectionsWindow : Window
{
    private readonly Func<IReadOnlyList<ChannelCollection>> _read;
    private readonly Func<string, Task<bool>> _create;
    private readonly Func<Guid, string, Task<bool>> _rename;
    private readonly Func<Guid, Task> _delete;
    private readonly ObservableCollection<CollectionRowView> _rows = [];

    internal CollectionsWindow(
        Func<IReadOnlyList<ChannelCollection>> read,
        Func<string, Task<bool>> create,
        Func<Guid, string, Task<bool>> rename,
        Func<Guid, Task> delete)
    {
        InitializeComponent();
        _read = read;
        _create = create;
        _rename = rename;
        _delete = delete;
        Reload();
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (var collection in _read())
        {
            _rows.Add(new CollectionRowView(collection));
        }

        CollectionList.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CollectionList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!await _create(NewNameBox.Text))
        {
            MessageBox.Show(this, LocalizationService.Get("CollectionNameInvalid"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NewNameBox.Clear();
        Reload();
    }

    private async void Rename_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            e.Handled = true;
            await ApplyRenameAsync(box);
        }
    }

    private async void Rename_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            await ApplyRenameAsync(box);
        }
    }

    private async Task ApplyRenameAsync(TextBox box)
    {
        if (box.Tag is not CollectionRowView row)
        {
            return;
        }

        var current = _read().FirstOrDefault(collection => collection.Id == row.Id);
        if (current is null || string.Equals(current.Name, box.Text, StringComparison.Ordinal))
        {
            return;
        }

        if (!await _rename(row.Id, box.Text))
        {
            // Blank or duplicate: put the stored name back so the list never shows an unsaved edit.
            row.Name = current.Name;
            MessageBox.Show(this, LocalizationService.Get("CollectionNameInvalid"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CollectionRowView row)
        {
            return;
        }

        if (MessageBox.Show(this, LocalizationService.Format("CollectionDeleteConfirm", row.Name), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _delete(row.Id);
        Reload();
    }
}
