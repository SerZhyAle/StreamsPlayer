using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

// SP-0017: local named collections. The rules live in Core (ChannelCollections); this file owns the
// filter combo, the per-channel membership menu, and persistence. Pinning is untouched: a collection
// never adds, removes, or reorders a pin, and the pinned band keeps its own section.
public partial class MainWindow
{
    private void PopulateCollectionFilter()
    {
        var selected = SelectedOptionValue(CollectionFilter) ?? AllValue;
        var items = new[] { new UiOption(AllValue, LocalizationService.Get("AllOption")) }
            .Concat(_state.Collections.Select(collection =>
                new UiOption(collection.Id.ToString(), collection.Name)))
            .ToArray();

        _updatingLocalizedOptions = true;
        try
        {
            CollectionFilter.ItemsSource = items;
            // A collection deleted while the app was closed falls back to All rather than an empty list.
            CollectionFilter.SelectedItem = items.FirstOrDefault(item => item.Value == selected) ?? items[0];
        }
        finally
        {
            _updatingLocalizedOptions = false;
        }
    }

    /// <summary>Members of the active collection, or null when the view is not filtered by one.</summary>
    private HashSet<Guid>? ActiveCollectionMembers()
    {
        var value = SelectedOptionValue(CollectionFilter);
        if (value is null or AllValue || !Guid.TryParse(value, out var collectionId))
        {
            return null;
        }

        var collection = _state.Collections.FirstOrDefault(item => item.Id == collectionId);
        return collection is null ? null : [.. collection.ChannelIds];
    }

    /// <summary>The "Add to collection" submenu for one channel: toggles, plus create and manage.</summary>
    private MenuItem BuildCollectionMenuItem(ChannelRow row)
    {
        var parent = new MenuItem { Header = LocalizationService.Get("CollectionMenu") };
        foreach (var collection in _state.Collections)
        {
            var item = new MenuItem
            {
                Header = collection.Name,
                IsCheckable = true,
                IsChecked = collection.ChannelIds.Contains(row.Channel.Id),
                Tag = (collection.Id, row.Channel.Id)
            };
            item.Click += CollectionMembership_Click;
            parent.Items.Add(item);
        }

        if (_state.Collections.Count > 0)
        {
            parent.Items.Add(new Separator());
        }

        // Inline name entry keeps "new collection" one gesture away instead of opening a screen.
        var nameBox = new TextBox { Width = 130, Margin = new Thickness(6, 0, 0, 0), MaxLength = ChannelCollections.MaximumNameLength };
        nameBox.Tag = row.Channel.Id;
        nameBox.KeyDown += NewCollectionBox_KeyDown;
        var newPanel = new StackPanel { Orientation = Orientation.Horizontal };
        newPanel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get("CollectionNew"),
            VerticalAlignment = VerticalAlignment.Center
        });
        newPanel.Children.Add(nameBox);
        parent.Items.Add(new MenuItem { Header = newPanel, StaysOpenOnClick = true });

        var manage = new MenuItem { Header = LocalizationService.Get("CollectionManage") };
        manage.Click += (_, _) => OpenCollectionsWindow();
        parent.Items.Add(manage);
        return parent;
    }

    private async void CollectionMembership_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem { Tag: ValueTuple<Guid, Guid> pair } item)
            {
                return;
            }

            var (collectionId, channelId) = pair;
            var member = item.IsChecked;
            await SaveCollectionsAsync(collections => member
                ? ChannelCollections.AddChannel(collections, collectionId, channelId)
                : ChannelCollections.RemoveChannel(collections, collectionId, channelId));
            _log.Event(member ? "COLLECTION ADD" : "COLLECTION REMOVE", $"collection={collectionId}");
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(CollectionMembership_Click), exception);
        }
    }

    private async void NewCollectionBox_KeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.Enter || sender is not TextBox { Tag: Guid channelId } box)
            {
                return;
            }

            e.Handled = true;
            var created = Guid.NewGuid();
            var name = box.Text;
            var createdCollection = ChannelCollections.Create(_state.Collections, name, created);
            if (createdCollection is null)
            {
                SetStatus("CollectionNameInvalid");
                return;
            }

            await SaveCollectionsAsync(collections =>
            {
                var createdNow = ChannelCollections.Create(collections, name, created);
                return createdNow is null ? collections : ChannelCollections.AddChannel(createdNow, created, channelId);
            });
            _log.Event("COLLECTION CREATE", $"collection={created}");
            SetStatus("CollectionCreated", ChannelCollections.NormalizeName(name)!);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(NewCollectionBox_KeyDown), exception);
        }
    }

    internal void OpenCollectionsWindow()
    {
        var window = new CollectionsWindow(
            () => _state.Collections,
            CreateCollectionAsync,
            RenameCollectionAsync,
            DeleteCollectionAsync)
        {
            Owner = DialogOwner
        };
        window.ShowDialog();
    }

    private async Task<bool> CreateCollectionAsync(string name)
    {
        var id = Guid.NewGuid();
        if (ChannelCollections.Create(_state.Collections, name, id) is null)
        {
            return false;
        }

        await SaveCollectionsAsync(collections => ChannelCollections.Create(collections, name, id) ?? collections);
        _log.Event("COLLECTION CREATE", $"id={id}");
        return true;
    }

    private async Task SetCollectionMembershipAsync(Guid collectionId, Guid channelId, bool member)
    {
        await SaveCollectionsAsync(collections => member
            ? ChannelCollections.AddChannel(collections, collectionId, channelId)
            : ChannelCollections.RemoveChannel(collections, collectionId, channelId));
        _log.Event(member ? "COLLECTION ADD" : "COLLECTION REMOVE", $"collection={collectionId}");
    }

    private async Task<bool> CreateCollectionWithChannelAsync(string name, Guid channelId)
    {
        var created = Guid.NewGuid();
        if (ChannelCollections.Create(_state.Collections, name, created) is null)
        {
            return false;
        }

        await SaveCollectionsAsync(collections =>
        {
            var createdNow = ChannelCollections.Create(collections, name, created);
            return createdNow is null ? collections : ChannelCollections.AddChannel(createdNow, created, channelId);
        });
        _log.Event("COLLECTION CREATE", $"collection={created}");
        return true;
    }

    private async Task<bool> RenameCollectionAsync(Guid id, string name)
    {
        if (ChannelCollections.Rename(_state.Collections, id, name) is null)
        {
            return false;
        }

        await SaveCollectionsAsync(collections => ChannelCollections.Rename(collections, id, name) ?? collections);
        _log.Event("COLLECTION RENAME", $"collection={id}");
        return true;
    }

    private async Task DeleteCollectionAsync(Guid id)
    {
        await SaveCollectionsAsync(collections => ChannelCollections.Delete(collections, id));
        _log.Event("COLLECTION DELETE", $"collection={id}");
    }

    /// <summary>Persists a new collection set and repaints everything that shows collections.</summary>
    private async Task SaveCollectionsAsync(
        Func<IReadOnlyList<ChannelCollection>, IReadOnlyList<ChannelCollection>> mutation)
    {
        await PersistAsync(state =>
        {
            var updated = mutation(state.Collections);
            return ReferenceEquals(updated, state.Collections) ? state : state with { Collections = [.. updated] };
        });
        PopulateCollectionFilter();
        ApplyFilter();
    }

    /// <summary>
    /// Drops memberships whose channel no longer exists - after a catalog refresh prunes rows, or a
    /// user row is deleted. Collections themselves always survive; only the user deletes those.
    /// </summary>
    private async Task PruneCollectionsAsync()
    {
        if (_state.Collections.Count == 0)
        {
            return;
        }

        var pruned = ChannelCollections.Prune(_state.Collections, _state.Channels.Select(channel => channel.Id));
        if (pruned.Zip(_state.Collections).All(pair => pair.First.ChannelIds.Count == pair.Second.ChannelIds.Count))
        {
            return;
        }

        await PersistAsync(state => state with
        {
            Collections = [.. ChannelCollections.Prune(state.Collections, state.Channels.Select(channel => channel.Id))]
        });
        PopulateCollectionFilter();
    }
}
