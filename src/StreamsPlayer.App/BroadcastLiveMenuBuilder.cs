using System.IO;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0217's always-visible indicator menu, shared by the main window and the compact panel: while any
/// source is live it names each one and offers its stop, its cast targets (the account's receivers
/// whose declared pairs cover one of its endpoints - a receiver that does not is shown as
/// unsupported, never offered) and its hand-off channels (link, import file).
/// </summary>
internal static class BroadcastLiveMenuBuilder
{
    /// <summary>
    /// How long the cast menu waits for the receivers before it shows what it has. SP-0222: the directory is not
    /// held while nobody looks, so opening the menu is what asks for it; a server that does not answer in this
    /// time costs the person an empty receiver list, not a frozen menu.
    /// </summary>
    internal static readonly TimeSpan ReceiversWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// <see cref="Build"/> after asking the account for its receivers once. The ask is a lease on the directory
    /// held for as long as the menu takes to build and given back at once: the menu keeps the receivers it
    /// captured, and nothing is followed afterwards.
    /// </summary>
    internal static async Task<ContextMenu> BuildAsync(BroadcastProducerService producer, ExchangeSourceService exchange,
        Window owner)
    {
        if (producer.Running.Count == 0 || !exchange.IsActive)
        {
            return Build(producer, exchange, owner);
        }

        using var lease = exchange.AcquireDirectory(follow: false);
        await exchange.WaitForDirectoryAsync(ReceiversWait);
        return Build(producer, exchange, owner);
    }

    internal static ContextMenu Build(BroadcastProducerService producer, ExchangeSourceService exchange, Window owner)
    {
        var menu = new ContextMenu();
        var running = producer.Running;
        if (running.Count == 0)
        {
            return menu;
        }

        foreach (var source in running)
        {
            var title = source == BroadcastSource.Microphone
                ? LocalizationService.Get("BroadcastMicTitle")
                : LocalizationService.Get("BroadcastSystemSoundTitle");
            var stop = new MenuItem
            {
                Header = LocalizationService.Format("BroadcastStopSource", title)
            };
            stop.Click += (_, _) => RunSafely(nameof(BroadcastLiveMenuBuilder), owner, () => producer.StopAsync(source));
            menu.Items.Add(stop);

            if (exchange.IsActive && exchange.CanCast)
            {
                // A cast goes through the exchange server, so the entry exists only while the account is on - and
                // only for a member's account, on a server that has not said it lacks cast (items AO, 7.2): a hidden
                // entry is better than one the server refuses.
                stop.Items.Add(BuildCastMenu(producer, exchange, source, owner));
            }

            stop.Items.Add(BuildShareItems(producer, source, title, owner));
        }

        menu.Items.Add(new Separator());
        var help = new MenuItem { Header = LocalizationService.Get("MediaExchange") };
        help.Click += (_, _) => ExchangeHelp.Open(owner, ExchangePortalPage.Broadcasts);
        menu.Items.Add(help);
        return menu;
    }

    private static MenuItem BuildCastMenu(BroadcastProducerService producer, ExchangeSourceService exchange,
        BroadcastSource source, Window owner)
    {
        var cast = new MenuItem { Header = LocalizationService.Get("BroadcastCastMenu") };
        var descriptor = producer.DescriptorText(source);
        var read = descriptor is null ? null : FastMediaSorterBroadcastDescriptor.Read(descriptor);
        var endpoints = read is { IsAccepted: true, Broadcast: not null } ? read.Broadcast.Endpoints : [];
        var own = exchange.Account.DeviceId;
        // A receiver broadcasts nothing, so it never appears in the broadcast groups: the cast menu
        // reads the directory's device list, which carries the declared pairs with it.
        foreach (var receiver in exchange.Directory?.Devices ?? [])
        {
            if (receiver.DeviceId == own || !receiver.IsOnline)
            {
                continue;
            }

            // Each cast is the user's act, never automatic; the eligibility judgment is the same one the
            // wire applies, so the greyed item and the refused offer cannot disagree.
            var eligible = ExchangeCastEligibility.CanOffer(receiver.Plays, endpoints);
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(receiver.DeviceName) ? receiver.DeviceId : receiver.DeviceName,
                IsEnabled = eligible
            };
            if (!eligible)
            {
                item.ToolTip = LocalizationService.Get("BroadcastCastUnsupported");
            }

            var captured = receiver;
            item.Click += (_, _) => RunSafely(nameof(BroadcastLiveMenuBuilder), owner, () => producer.CastAsync(source, captured));
            cast.Items.Add(item);
        }

        if (cast.Items.Count == 0)
        {
            cast.IsEnabled = false;
            cast.ToolTip = LocalizationService.Get("BroadcastCastNoReceivers");
        }

        return cast;
    }

    private static MenuItem BuildShareItems(BroadcastProducerService producer, BroadcastSource source, string title, Window owner)
    {
        var share = new MenuItem { Header = LocalizationService.Get("BroadcastShareMenu") };
        var link = new MenuItem { Header = LocalizationService.Get("BroadcastCopyLink") };
        link.Click += (_, _) =>
        {
            try
            {
                if (producer.DescriptorText(source) is { } descriptor)
                {
                    Clipboard.SetText(FastMediaSorterBroadcastShare.Link(descriptor));
                }
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(BroadcastLiveMenuBuilder), exception);
            }
        };
        share.Items.Add(link);

        var file = new MenuItem { Header = LocalizationService.Get("BroadcastSaveFile") };
        file.Click += (_, _) => RunSafely(nameof(BroadcastLiveMenuBuilder), owner, () =>
        {
            if (producer.DescriptorText(source) is not { } descriptor)
            {
                return Task.CompletedTask;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = FastMediaSorterBroadcastShare.SuggestedFileName(1),
                Filter = LocalizationService.Get("BroadcastFileFilter")
            };
            if (dialog.ShowDialog(owner) == true)
            {
                File.WriteAllText(dialog.FileName, FastMediaSorterBroadcastShare.ImportFileContent(descriptor));
            }

            return Task.CompletedTask;
        });
        share.Items.Add(file);
        return share;
    }

    private static void RunSafely(string name, Window owner, Func<Task> action)
    {
        _ = owner;
        try
        {
            _ = action();
        }
        catch (Exception exception)
        {
            HandlerBoundary.Report(name, exception);
        }
    }
}
