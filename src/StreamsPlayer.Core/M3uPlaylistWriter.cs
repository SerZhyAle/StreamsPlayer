using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Serialises channels to a portable extended-M3U body (UTF-8 text, no BOM added here). Titles are written
/// on an <c>#EXTINF:-1,</c> line with newlines flattened so one channel never spans playlist entries; the
/// URL follows verbatim so a round-trip through <see cref="M3uPlaylistParser"/> preserves title and order.
/// </summary>
public static class M3uPlaylistWriter
{
    public static string Write(IEnumerable<StreamChannel> channels)
    {
        var builder = new StringBuilder();
        builder.Append("#EXTM3U\r\n");
        foreach (var channel in channels)
        {
            var title = channel.Title.Replace('\r', ' ').Replace('\n', ' ').Trim();
            builder.Append("#EXTINF:-1,").Append(title).Append("\r\n");
            builder.Append(channel.Url).Append("\r\n");
        }

        return builder.ToString();
    }
}
