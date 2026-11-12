using System.Windows.Threading;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0250 requirement 1: while a window plays a broadcast that came through an exchange server, the account's directory
/// is followed, so the server's word that the broadcast ended reaches the window (<c>LIVE-BROADCAST</c> item U). Playing
/// through the relay or the tunnel subscribes nothing by itself, and a removal is pushed only to a subscribed control stream.
/// <para>
/// The own account follows through the same lease the broadcast list takes
/// (<see cref="ExchangeSourceService.AcquireDirectory"/>): the session lists, subscribes once, lists again after it is
/// replaced and after a gap, and the removal is read from the list that follows. A guest entry has no session of this
/// program's own, so it follows through one read-only viewer (SP-0222). Both are held only while a window is registered and
/// are given back with the last one - and neither starts a broadcast or a list the person did not ask for (item BL: playing
/// is the person's act). A channel with no directory origin (a link, a file, a Drive entry) follows nothing and tells its end
/// by the answers of the reconnect.
/// </para>
/// A removal is told once per absence, on the UI thread.
/// </summary>
internal sealed class BroadcastEndFollower(ExchangeSourceService exchange, Dispatcher dispatcher)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FollowedSource> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts following the directory for <paramref name="channel"/>'s broadcast. Returns the registration to dispose when the
    /// window stops playing, or null when the channel has no directory origin this program can follow.
    /// </summary>
    /// <param name="onRemoval">Called on the UI thread when the broadcast leaves the directory, at most once per absence.</param>
    internal IDisposable? Follow(StreamChannel channel, Action onRemoval)
    {
        if (channel.FastMediaSorterBroadcast is not
            { DirectoryBroadcastId: { Length: > 0 } broadcastId, DirectoryEntryIdentity: { Length: > 0 } identity })
        {
            return null;
        }

        var registration = new Registration(new BroadcastRemovalReader(broadcastId), onRemoval);
        lock (_gate)
        {
            if (!_sources.TryGetValue(identity, out var source))
            {
                source = OpenSource(identity);
                if (source is null)
                {
                    return null;
                }

                _sources[identity] = source;
            }

            source.Registrations.Add(registration);
        }

        // The baseline: a directory already held answers now whether the broadcast is listed, so a later absence is a removal.
        Evaluate(identity);
        return new Release(() => Unfollow(identity, registration));
    }

    private FollowedSource? OpenSource(string identity)
    {
        if (exchange.IsActive
            && identity == ExchangeAccountIdentity.Key(exchange.Account.Host, exchange.Account.Port, exchange.Account.Login))
        {
            void OnDirectoryChanged() => Evaluate(identity);
            exchange.DirectoryChanged += OnDirectoryChanged;
            var lease = exchange.AcquireDirectory(follow: true);
            return new FollowedSource(() => exchange.Directory, () =>
            {
                exchange.DirectoryChanged -= OnDirectoryChanged;
                lease.Dispose();
            });
        }

        var guest = exchange.Guests.Entries.FirstOrDefault(entry =>
            identity == ExchangeAccountIdentity.Key(entry.Host, entry.Port, entry.Login));
        if (guest is null)
        {
            return null;
        }

        void OnViewerChanged() => Evaluate(identity);
        var viewer = exchange.OpenGuestViewer(guest);
        viewer.Changed += OnViewerChanged;
        return new FollowedSource(() => viewer.Snapshot, () =>
        {
            viewer.Changed -= OnViewerChanged;
            viewer.Dispose();
        });
    }

    private void Evaluate(string identity)
    {
        Registration[] registrations;
        Func<ExchangeDirectorySnapshot?> snapshotOf;
        lock (_gate)
        {
            if (!_sources.TryGetValue(identity, out var source))
            {
                return;
            }

            registrations = [.. source.Registrations];
            snapshotOf = source.SnapshotOf;
        }

        var snapshot = snapshotOf();
        foreach (var registration in registrations)
        {
            if (registration.Reader.Observe(snapshot))
            {
                Post(registration);
            }
        }
    }

    // The service tells its change from its own read loop; whatever a window does with it must not run there, and must not
    // be able to fault it.
    private void Post(Registration registration)
    {
        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (registration.Released)
            {
                return;
            }

            try
            {
                registration.OnRemoval();
            }
            catch (Exception exception)
            {
                HandlerBoundary.Report(nameof(BroadcastEndFollower), exception, notifyUser: false);
            }
        });
    }

    private void Unfollow(string identity, Registration registration)
    {
        registration.Released = true;
        FollowedSource? closing = null;
        lock (_gate)
        {
            if (_sources.TryGetValue(identity, out var source) && source.Registrations.Remove(registration)
                && source.Registrations.Count == 0)
            {
                _sources.Remove(identity);
                closing = source;
            }
        }

        closing?.Close();
    }

    private sealed class Registration(BroadcastRemovalReader reader, Action onRemoval)
    {
        internal BroadcastRemovalReader Reader { get; } = reader;
        internal Action OnRemoval { get; } = onRemoval;
        internal volatile bool Released;
    }

    private sealed class FollowedSource(Func<ExchangeDirectorySnapshot?> snapshotOf, Action close)
    {
        internal List<Registration> Registrations { get; } = [];
        internal Func<ExchangeDirectorySnapshot?> SnapshotOf { get; } = snapshotOf;
        internal void Close() => close();
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }
}
