using System.Globalization;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>Bounded RTSP messages and interleaved RTP/RTCP; HTTP uses only the initial request head.</summary>
internal sealed record DigestRtspMessage(string StartLine, List<KeyValuePair<string, string>> Headers, byte[] Body, byte[]? Frame = null)
{
    internal string? Header(string name) => Headers.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    internal IEnumerable<string> Values(string name) => Headers.Where(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value);

    internal static async Task<DigestRtspMessage?> ReadAsync(Stream input, CancellationToken token)
    {
        var one = new byte[1];
        if (await input.ReadAsync(one, token).ConfigureAwait(false) == 0) return null;
        if (one[0] == '$')
        {
            var prefix = new byte[4];
            prefix[0] = (byte)'$';
            await input.ReadExactlyAsync(prefix.AsMemory(1), token).ConfigureAwait(false);
            var frame = new byte[4 + (prefix[2] << 8 | prefix[3])];
            prefix.CopyTo(frame, 0);
            await input.ReadExactlyAsync(frame.AsMemory(4), token).ConfigureAwait(false);
            return new("", [], [], frame);
        }
        using var head = new MemoryStream();
        head.WriteByte(one[0]);
        var ending = (int)one[0];
        while (head.Length < 16 * 1024)
        {
            await input.ReadExactlyAsync(one, token).ConfigureAwait(false);
            head.WriteByte(one[0]);
            ending = (ending << 8) | one[0];
            if (ending == 0x0D0A0D0A) break;
        }
        if (ending != 0x0D0A0D0A) throw new InvalidDataException("Broadcast head exceeds limit.");
        var lines = Encoding.UTF8.GetString(head.ToArray()).Split("\r\n");
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidDataException("Malformed broadcast head.");
            headers.Add(new(line[..colon], line[(colon + 1)..].Trim()));
        }
        var lengths = headers.Where(pair => pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).ToArray();
        var length = 0;
        if (lengths.Length > 1 || lengths.Length == 1 && (!int.TryParse(lengths[0].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out length) || length > 256 * 1024))
            throw new InvalidDataException("Invalid broadcast body length.");
        var body = new byte[length];
        await input.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return new(lines[0], headers, body);
    }

    internal async Task WriteAsync(Stream output, CancellationToken token)
    {
        if (Frame is not null) { await output.WriteAsync(Frame, token).ConfigureAwait(false); return; }
        var head = new StringBuilder(StartLine + "\r\n");
        foreach (var (name, value) in Headers.Where(pair => !pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        if (Body.Length > 0 || Header("Content-Length") is not null) head.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
        head.Append("\r\n");
        await output.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), token).ConfigureAwait(false);
        if (Body.Length > 0) await output.WriteAsync(Body, token).ConfigureAwait(false);
    }
}
