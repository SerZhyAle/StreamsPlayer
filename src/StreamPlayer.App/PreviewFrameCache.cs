using System.Windows.Media;
using StreamPlayer.Core;

namespace StreamPlayer.App;

public sealed class PreviewFrameCache(int capacity, TimeSpan freshness, Action<string>? evicted = null)
{
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = [];
    private readonly object _gate = new();

    public bool TryGet(string url, out ImageSource? image)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(url, out var entry))
            {
                image = null;
                return false;
            }

            Touch(url, entry);
            image = entry.Image;
            return true;
        }
    }

    public bool IsFresh(string url)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(url, out var entry) &&
                entry.IsLive && DateTimeOffset.UtcNow - entry.CapturedAt < freshness;
        }
    }

    public void SeedRestored(string url, ImageSource image) => Put(url, image, DateTimeOffset.MinValue, false);

    public void PutLive(string url, ImageSource image) => Put(url, image, DateTimeOffset.UtcNow, true);

    private void Put(string url, ImageSource image, DateTimeOffset capturedAt, bool isLive)
    {
        string? evictedUrl = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var existing))
            {
                _recency.Remove(existing.Node);
            }

            var node = _recency.AddFirst(url);
            _entries[url] = new CacheEntry(image, capturedAt, isLive, node);
            while (_entries.Count > capacity && _recency.Last is { } oldest)
            {
                evictedUrl = oldest.Value;
                _entries.Remove(evictedUrl);
                _recency.RemoveLast();
            }
        }
        if (evictedUrl is not null)
        {
            evicted?.Invoke(evictedUrl);
        }
    }

    private void Touch(string url, CacheEntry entry)
    {
        _recency.Remove(entry.Node);
        var node = _recency.AddFirst(url);
        _entries[url] = entry with { Node = node };
    }

    private sealed record CacheEntry(
        ImageSource Image,
        DateTimeOffset CapturedAt,
        bool IsLive,
        LinkedListNode<string> Node);
}

public static class PreviewCapturePolicy
{
    public static bool IsCaptureable(StreamChannel channel) =>
        channel.MediaKind == MediaKind.Video &&
        Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
