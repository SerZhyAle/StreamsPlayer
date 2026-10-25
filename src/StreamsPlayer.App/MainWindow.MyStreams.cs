using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public sealed class MyStreamsDeviceGroupModel(
    string deviceId,
    string deviceName,
    string presenceText,
    IEnumerable<MyStreamsBroadcastItemModel> broadcasts) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string DeviceId { get; } = deviceId;
    public string DeviceName { get; } = deviceName;
    public string PresenceText { get; } = presenceText;
    public ObservableCollection<MyStreamsBroadcastItemModel> Broadcasts { get; } = new(broadcasts);

    public override string ToString() => $"{DeviceName} {PresenceText}";
}

public sealed class MyStreamsBroadcastItemModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ExchangeBroadcastView View { get; }
    public StreamChannel? MatchedSavedChannel { get; }
    internal IncomingBroadcastEntry? OpenLeg { get; }

    internal MyStreamsBroadcastItemModel(
        ExchangeBroadcastView view,
        StreamChannel? matchedSavedChannel,
        IncomingBroadcastEntry? openLeg)
    {
        View = view;
        MatchedSavedChannel = matchedSavedChannel;
        OpenLeg = openLeg;
    }

    public string Title => !string.IsNullOrWhiteSpace(View.Record.Title)
        ? View.Record.Title!
        : MatchedSavedChannel is not null && !string.IsNullOrWhiteSpace(MatchedSavedChannel.Title)
            ? MatchedSavedChannel.Title
            : ModeLabel;

    public string Detail => BuildDetail();

    public bool IsSaved => MatchedSavedChannel is not null;

    public bool IsVideo =>
        View.Record.Descriptor?.IsVideoMode == true ||
        View.Record.Mode is FastMediaSorterBroadcastDescriptor.VideoAudioMode or FastMediaSorterBroadcastDescriptor.VideoOnlyMode;

    public string PlayCaption => IsVideo
        ? LocalizationService.Get("MyStreamsWatch")
        : LocalizationService.Get("MyStreamsListen");

    public Visibility PlayVisibility =>
        View.Support == ExchangeBroadcastSupport.Supported &&
        ExchangeBroadcastKeys.FailureOf(View.Record.BroadcastId) == ExchangeSealedState.None
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility KeepVisibility =>
        IsSaved || PlayVisibility != Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility SavedBadgeVisibility => IsSaved ? Visibility.Visible : Visibility.Collapsed;

    public string PlayName => LocalizationService.Format("BroadcastsPlayNamed", Title);

    public string KeepName => LocalizationService.Format("BroadcastsKeepNamed", Title);

    private string ModeLabel => View.Record.Mode switch
    {
        FastMediaSorterBroadcastDescriptor.VideoAudioMode => LocalizationService.Get("BroadcastsVideo"),
        FastMediaSorterBroadcastDescriptor.VideoOnlyMode => LocalizationService.Get("BroadcastsVideoOnly"),
        _ => LocalizationService.Get("BroadcastsAudio")
    };

    private string BuildDetail()
    {
        var mode = ModeLabel;
        return View.Support switch
        {
            ExchangeBroadcastSupport.Unsupported => LocalizationService.Get("BroadcastsUnsupportedReason"),
            ExchangeBroadcastSupport.UnsupportedSchema => LocalizationService.Get(ExchangeFailureRows.FormatUnsupported.MessageKey),
            ExchangeBroadcastSupport.Unsealed => LocalizationService.Get("BroadcastsUnsealedReason"),
            _ when ExchangeBroadcastKeys.FailureOf(View.Record.BroadcastId) is var sealedState && sealedState != ExchangeSealedState.None => LocalizationService.Get(sealedState.MessageKey()),
            _ => OpenLeg is null ? mode : $"{mode}, {StateText(OpenLeg)}"
        };
    }

    private static string StateText(IncomingBroadcastEntry entry) => LocalizationService.Get(entry.State switch
    {
        BroadcastLegState.Connecting => "BroadcastsStateConnecting",
        BroadcastLegState.Playing => "BroadcastsStatePlaying",
        BroadcastLegState.Reconnecting => "BroadcastsStateReconnecting",
        BroadcastLegState.Ended => "BroadcastsStateEnded",
        BroadcastLegState.Failed => "BroadcastsStateFailed",
        _ => "BroadcastsStateStopped"
    });

    public override string ToString() => $"{Title}, {Detail}";
}

public partial class MainWindow
{
    private IDisposable? _myStreamsDirectoryLease;
    private bool _myStreamsRefreshing;
    private bool _myStreamsRefreshFailed;

    public ObservableCollection<MyStreamsDeviceGroupModel> MyStreamsGroups { get; } = [];

    public bool IsMyStreamsActive => (SelectedOptionValue(MediaFilter) ?? AllValue) == OwnFilterValue;

    public Visibility DeviceBroadcastsVisibility => IsMyStreamsActive ? Visibility.Visible : Visibility.Collapsed;

    public int MyStreamsBroadcastCount => MyStreamsGroups.Sum(group => group.Broadcasts.Count);

    private void UpdateMyStreamsLifecycle(bool isActive)
    {
        if (isActive)
        {
            if (_myStreamsDirectoryLease is null)
            {
                Exchange.DirectoryChanged += OnMyStreamsDirectoryChanged;
                IncomingBroadcastsChanged += OnMyStreamsIncomingChanged;
                _myStreamsDirectoryLease = Exchange.AcquireDirectory(follow: true);
                _myStreamsRefreshFailed = false;
            }

            RebuildMyStreamsDeviceGroups();
        }
        else
        {
            if (_myStreamsDirectoryLease is not null)
            {
                _myStreamsDirectoryLease.Dispose();
                _myStreamsDirectoryLease = null;
                Exchange.DirectoryChanged -= OnMyStreamsDirectoryChanged;
                IncomingBroadcastsChanged -= OnMyStreamsIncomingChanged;
                MyStreamsGroups.Clear();
                _myStreamsRefreshFailed = false;
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMyStreamsActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeviceBroadcastsVisibility)));

        if (MainHeaderText is not null)
        {
            MainHeaderText.SetResourceReference(TextBlock.TextProperty,
                isActive ? "MyStreamsSavedChannelsTitle" : "MainSectionHeader");
        }

        if (MainHeaderButton is not null)
        {
            MainHeaderButton.SetResourceReference(AutomationProperties.NameProperty,
                isActive ? "MyStreamsSavedChannelsTitle" : "MainSectionHeader");
        }
    }

    private void RebuildMyStreamsDeviceGroups()
    {
        if (!IsMyStreamsActive)
        {
            MyStreamsGroups.Clear();
            return;
        }

        if (DeviceBroadcastsSection is null)
        {
            return;
        }

        if (!Exchange.HasAccount)
        {
            MyStreamsNoServerPanel.Visibility = Visibility.Visible;
            MyStreamsEmptyPanel.Visibility = Visibility.Collapsed;
            MyStreamsStatusText.Text = string.Empty;
            MyStreamsRefreshButton.Visibility = Visibility.Collapsed;
            MyStreamsRetryButton.Visibility = Visibility.Collapsed;
            MyStreamsGroups.Clear();
            return;
        }

        MyStreamsNoServerPanel.Visibility = Visibility.Collapsed;
        MyStreamsRefreshButton.Visibility = Visibility.Visible;

        if (_myStreamsRefreshing)
        {
            MyStreamsStatusText.Text = LocalizationService.Get("MyStreamsRefreshing");
            MyStreamsRetryButton.Visibility = Visibility.Collapsed;
        }
        else if (_myStreamsRefreshFailed)
        {
            MyStreamsStatusText.Text = LocalizationService.Get("MyStreamsRefreshFailed");
            MyStreamsRetryButton.Visibility = Visibility.Visible;
        }
        else if (Exchange.Directory is not null && !Exchange.IsDirectoryFresh)
        {
            MyStreamsStatusText.Text = LocalizationService.Get("MyStreamsCached");
            MyStreamsRetryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MyStreamsStatusText.Text = string.Empty;
            MyStreamsRetryButton.Visibility = Visibility.Collapsed;
        }

        var snapshot = Exchange.Directory;
        if (snapshot is null)
        {
            if (!_myStreamsRefreshFailed)
            {
                MyStreamsStatusText.Text = LocalizationService.Get("MyStreamsRefreshing");
            }
            MyStreamsEmptyPanel.Visibility = Visibility.Collapsed;
            MyStreamsGroups.Clear();
            return;
        }

        var ownDeviceId = Exchange.Account?.DeviceId ?? string.Empty;
        var filteredSnapshot = ExchangeDirectoryState.HideOwnDevice(snapshot, ownDeviceId);

        var ownAccountIdentity = Exchange.HasAccount
            ? ExchangeAccountIdentity.Key(Exchange.Account.Host, Exchange.Account.Port, Exchange.Account.Login)
            : null;

        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var incoming = IncomingBroadcasts;
        var groupsList = new List<MyStreamsDeviceGroupModel>();

        foreach (var group in filteredSnapshot.Groups)
        {
            var broadcastItems = new List<MyStreamsBroadcastItemModel>();
            foreach (var view in group.Broadcasts)
            {
                var matchedChannel = MyStreamsMatch.FindMatchingSavedChannel(_state.Channels, view, ownAccountIdentity);
                if (!MyStreamsMatch.MatchesSearch(group.DeviceName, view, matchedChannel, query))
                {
                    continue;
                }

                var open = view.Record.Descriptor?.SourceId is { } sourceId
                    ? incoming.FirstOrDefault(entry => entry.SourceId == sourceId)
                    : null;

                var item = new MyStreamsBroadcastItemModel(view, matchedChannel, open);
                broadcastItems.Add(item);
            }

            if (broadcastItems.Count > 0)
            {
                var presence = LocalizationService.Get(group.IsOnline ? "BroadcastsOnline" : "BroadcastsOffline");
                if (_myStreamsRefreshFailed || !Exchange.IsDirectoryFresh)
                {
                    presence += $" - {LocalizationService.Get("MyStreamsUnverified")}";
                }

                var groupModel = new MyStreamsDeviceGroupModel(group.DeviceId, group.DeviceName, presence, broadcastItems);
                groupsList.Add(groupModel);
            }
        }

        MyStreamsGroups.Clear();
        foreach (var g in groupsList)
        {
            MyStreamsGroups.Add(g);
        }

        if (MyStreamsGroups.Count == 0)
        {
            MyStreamsEmptyPanel.Visibility = Visibility.Visible;
            MyStreamsEmptyText.Text = string.IsNullOrEmpty(query)
                ? LocalizationService.Get("MyStreamsNoDevices")
                : LocalizationService.Get("MyStreamsNoMatches");
        }
        else
        {
            MyStreamsEmptyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMyStreamsDirectoryChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (IsMyStreamsActive)
            {
                RebuildMyStreamsDeviceGroups();
                ApplyFilter();
            }
        });
    }

    private void OnMyStreamsIncomingChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (IsMyStreamsActive)
            {
                RebuildMyStreamsDeviceGroups();
            }
        });
    }

    private async Task RefreshMyStreamsAsync()
    {
        if (_myStreamsRefreshing || !Exchange.HasAccount)
        {
            return;
        }

        _myStreamsRefreshing = true;
        _myStreamsRefreshFailed = false;
        RebuildMyStreamsDeviceGroups();

        try
        {
            var answered = await Exchange.RefreshDirectoryAsync(TimeSpan.FromSeconds(5));
            _myStreamsRefreshFailed = !answered;
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(RefreshMyStreamsAsync), exception, notifyUser: false);
            _myStreamsRefreshFailed = true;
        }
        finally
        {
            _myStreamsRefreshing = false;
            RebuildMyStreamsDeviceGroups();
            ApplyFilter();
        }
    }

    private async void MyStreamsRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshMyStreamsAsync();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MyStreamsRefreshButton_Click), exception);
        }
    }

    private async void MyStreamsRetryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshMyStreamsAsync();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MyStreamsRetryButton_Click), exception);
        }
    }

    private async void MyStreamsPlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: MyStreamsBroadcastItemModel item })
            {
                await OpenExchangeBroadcastWindowAsync(item.View);
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MyStreamsPlay_Click), exception);
        }
    }

    private async void MyStreamsKeep_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: MyStreamsBroadcastItemModel item })
            {
                await KeepExchangeBroadcastAsync(item.View);
                RebuildMyStreamsDeviceGroups();
                ApplyFilter();
            }
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MyStreamsKeep_Click), exception);
        }
    }

    private void MyStreamsConnectServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OpenSettings(SettingsWindow.ExchangePageIndex);
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(nameof(MyStreamsConnectServer_Click), exception);
        }
    }
}
