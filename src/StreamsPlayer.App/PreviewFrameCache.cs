using System.Windows.Media;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public sealed class PreviewFrameCache(int capacity, Action<string>? evicted = null)
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

    public void Put(string url, ImageSource image)
    {
        string? evictedUrl = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var existing))
            {
                _recency.Remove(existing.Node);
            }

            var node = _recency.AddFirst(url);
            _entries[url] = new CacheEntry(image, node);
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

    private sealed record CacheEntry(ImageSource Image, LinkedListNode<string> Node);
}

public static class PreviewCapturePolicy
{
    public static bool IsCaptureable(StreamChannel channel) =>
        channel.MediaKind == MediaKind.Video &&
        Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
