using System.IO;
using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// The hand-off menus of a live source in the broadcast window (SP-0252 requirement 4): the share items (link, password,
/// QR code, description file) and the cast targets (the account's receivers whose declared pairs cover one of the
/// source's endpoints - a receiver that does not is shown as unsupported, never offered). They are the actions the
/// live indicator's menu had; the window only asks for them where the source's card is.
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
    /// The cast menu of one live source, after asking the account for its receivers once. The ask is a lease on the
    /// directory held for as long as the menu takes to build and given back at once: the menu keeps the receivers it
    /// captured, and nothing is followed afterwards.
    /// </summary>
    internal static async Task<ContextMenu> BuildCastMenuAsync(BroadcastProducerService producer, ExchangeSourceService exchange,
        BroadcastSource source, Window owner)
    {
        if (exchange.IsActive)
        {
            using var lease = exchange.AcquireDirectory(follow: false);
            await exchange.WaitForDirectoryAsync(ReceiversWait);
            return BuildCastMenu(producer, exchange, source, owner);
        }

        return BuildCastMenu(producer, exchange, source, owner);
    }

    internal static ContextMenu BuildCastMenu(BroadcastProducerService producer, ExchangeSourceService exchange,
        BroadcastSource source, Window owner)
    {
        var menu = new ContextMenu();
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
                // S6-6: a disabled item shows no tooltip unless it is asked to, and this one is the explanation.
                ToolTipService.SetShowOnDisabled(item, true);
            }

            var captured = receiver;
            item.Click += (_, _) => RunSafely(nameof(BroadcastLiveMenuBuilder), () => producer.CastAsync(source, captured));
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            // An empty menu would not open at all; the one disabled line says why there is nothing to pick.
            var none = new MenuItem { Header = LocalizationService.Get("BroadcastCastNoReceivers"), IsEnabled = false };
            menu.Items.Add(none);
        }

        return menu;
    }

    internal static ContextMenu BuildShareMenu(BroadcastProducerService producer, BroadcastSource source, Window owner)
    {
        var menu = new ContextMenu();
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
        menu.Items.Add(link);

        // Item R: the password is the person's to tell, apart from the link. With "none" there is nothing to copy.
        var password = new MenuItem { Header = LocalizationService.Get("BroadcastCopyPassword") };
        if (producer.LinkPassword(source) is null)
        {
            password.IsEnabled = false;
            password.ToolTip = LocalizationService.Get("BroadcastLinkNoneWarning");
            ToolTipService.SetShowOnDisabled(password, true); // S6-6
        }

        password.Click += (_, _) =>
        {
            try
            {
                if (producer.LinkPassword(source) is { } secret)
                {
                    SecretClipboard.SetText(secret);
                }
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(BroadcastLiveMenuBuilder), exception);
            }
        };
        menu.Items.Add(password);

        var qr = new MenuItem { Header = LocalizationService.Get("BroadcastShowQr") };
        qr.Click += (_, _) =>
        {
            try
            {
                if (producer.DescriptorText(source) is { } descriptor)
                {
                    new BroadcastQrWindow(descriptor) { Owner = owner }.ShowDialog();
                }
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(BroadcastLiveMenuBuilder), exception);
            }
        };
        menu.Items.Add(qr);

        var file = new MenuItem { Header = LocalizationService.Get("BroadcastSaveFile") };
        file.Click += (_, _) => RunSafely(nameof(BroadcastLiveMenuBuilder), () =>
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
        menu.Items.Add(file);
        return menu;
    }

    private static void RunSafely(string name, Func<Task> action) =>
        // S6-5: the boundary awaits the task, so a fault in CastAsync is logged and reported once. A bare `_ = action()`
        // would catch only a synchronous throw and drop the task, and with it every later fault.
        HandlerBoundary.Run(name, action);
}
