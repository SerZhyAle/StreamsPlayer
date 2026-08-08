using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0074: the status line and headers of a station's reply, read from a raw stream.
/// </summary>
/// <remarks>
/// Exists because a Shoutcast v1 daemon greets with <c>ICY 200 OK</c> and .NET's HTTP stack refuses the
/// response before reading a single header - measured, not assumed. This accepts that greeting and the
/// ordinary one, and understands nothing else about HTTP: no chunking, no redirects, no keep-alive. The
/// caller asks for <c>HTTP/1.0</c> with <c>Connection: close</c> precisely so that none of those exist.
///
/// <para>Every bound here is a limit on what an untrusted server can make this process do, and all of
/// them are in from the start rather than added after a bad station finds them. Reading stops at the
/// first violation and reports nothing, because a half-parsed head is not a usable one.</para>
/// </remarks>
public sealed class IcyResponseHead
{
    /// <summary>A header line longer than this is not a header.</summary>
    public const int MaxLineLength = 4096;

    /// <summary>More headers than any station has a reason to send.</summary>
    public const int MaxHeaders = 64;

    /// <summary>
    /// The whole head. Without this, a server that never sends the blank line terminator would be read
    /// until the connection died or the process ran out of memory.
    /// </summary>
    public const int MaxHeadBytes = 16384;

    private readonly Dictionary<string, string> _headers;

    private IcyResponseHead(int statusCode, Dictionary<string, string> headers)
    {
        StatusCode = statusCode;
        _headers = headers;
    }

    public int StatusCode { get; }

    /// <summary>The header value, or null when absent. Case-insensitive, as HTTP headers are.</summary>
    public string? this[string name] => _headers.GetValueOrDefault(name);

    /// <summary>
    /// Reads the head from <paramref name="stream"/>, leaving it positioned on the first body byte.
    /// Returns null when the reply is not one this reader can use - including when it never terminates.
    /// </summary>
    public static async Task<IcyResponseHead?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var statusLine = await ReadLineAsync(stream, MaxHeadBytes, cancellationToken);
        if (statusLine is null || !TryParseStatus(statusLine.Value.Line, out var statusCode))
        {
            return null;
        }

        var consumed = statusLine.Value.Bytes;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, MaxHeadBytes - consumed, cancellationToken);
            if (line is null)
            {
                return null; // ran past the head budget, or the stream ended mid-head
            }

            consumed += line.Value.Bytes;
            if (line.Value.Line.Length == 0)
            {
                return new IcyResponseHead(statusCode, headers); // blank line: the head is over
            }

            if (headers.Count >= MaxHeaders)
            {
                return null;
            }

            var separator = line.Value.Line.IndexOf(':');
            if (separator <= 0)
            {
                continue; // a line with no name is not a header; skip it rather than fail the reply
            }

            var name = line.Value.Line[..separator].Trim();
            var value = line.Value.Line[(separator + 1)..].Trim();
            if (name.Length > 0)
            {
                // First value wins: a duplicated header is a broken server, and the first is what a
                // conventional client would have used.
                _ = headers.TryAdd(name, value);
            }
        }
    }

    /// <summary>Accepts both <c>HTTP/1.x 200 ..</c> and <c>ICY 200 OK</c>.</summary>
    private static bool TryParseStatus(string line, out int statusCode)
    {
        statusCode = 0;
        var firstSpace = line.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return false;
        }

        var protocol = line[..firstSpace];
        if (!protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) &&
            !protocol.Equals("ICY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = line[(firstSpace + 1)..].TrimStart();
        var codeEnd = rest.IndexOf(' ');
        var code = codeEnd < 0 ? rest : rest[..codeEnd];
        return int.TryParse(code, out statusCode);
    }

    /// <summary>
    /// One CRLF- or LF-terminated line, byte at a time. Byte-at-a-time is deliberate: a buffered reader
    /// would swallow body bytes past the head, and the body here is a binary audio frame whose first
    /// byte matters.
    /// </summary>
    private static async Task<(string Line, int Bytes)?> ReadLineAsync(
        Stream stream,
        int budget,
        CancellationToken cancellationToken)
    {
        if (budget <= 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        var single = new byte[1];
        var read = 0;
        while (read < budget)
        {
            if (await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken) == 0)
            {
                return null; // the stream ended inside the head
            }

            read++;
            var value = (char)single[0];
            if (value == '\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return (builder.ToString(), read);
            }

            if (builder.Length >= MaxLineLength)
            {
                return null;
            }

            builder.Append(value);
        }

        return null; // no terminator within the budget
    }
}
