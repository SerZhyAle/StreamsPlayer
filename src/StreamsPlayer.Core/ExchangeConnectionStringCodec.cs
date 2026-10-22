using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core;

public sealed class ExchangeConnectionString(string host, int port, string? fingerprint = null,
    string? name = null, string? login = null, bool hasInvitation = false)
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string? Fingerprint { get; } = fingerprint;
    public string? Name { get; } = name;
    public string? Login { get; } = login;

    /// <summary>
    /// The string carried a well-formed one-time invitation (item AR, key <c>i</c>). Only the fact is kept:
    /// this product signs in and never registers (item AZ), so the code itself is validated and dropped - it
    /// is a secret until used and has no consumer here, which is also why it can never reach a log.
    /// </summary>
    public bool HasInvitation { get; } = hasInvitation;
}

public sealed class ExchangeConnectionStringResult
{
    internal ExchangeConnectionStringResult(ExchangeConnectionString? data, string? reason)
    {
        Data = data;
        Reason = reason;
    }

    public ExchangeConnectionString? Data { get; }
    public string? Reason { get; }
    public bool Success => Data is not null;
}

/// <summary>DEVICE-EXCHANGE 0.18 section 5.5. Refusals carry only the contract's reason vocabulary.</summary>
public static class ExchangeConnectionStringCodec
{
    public const string Prefix = "FMSEXCH1:";
    public const int MaximumTextLength = 512;
    public const int MaximumFileBytes = 4096;
    public const int MaximumNameScalars = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ExchangeConnectionStringResult Parse(string? text)
    {
        text = Trim(text ?? "");
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Refused(text.Length >= Prefix.Length && text.StartsWith("FMSEXCH", StringComparison.Ordinal)
                && text[7] is >= '2' and <= '9' && text[8] == ':' ? "newer-major" : "not-a-string");
        }

        if (text.Length > MaximumTextLength)
            return Refused("too-long");
        if (text.Any(char.IsWhiteSpace))
            return Refused("whitespace");
        if (text.Any(c => c is < ' ' or > '~'))
            return Refused("not-ascii");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = text[Prefix.Length..];
        if (body.Length > 0)
        {
            foreach (var field in body.Split('&'))
            {
                var separator = field.IndexOf('=');
                if (separator < 1)
                    return Refused("malformed-field");
                var key = field[..separator];
                if (key is not ("x" or "fp" or "n" or "u" or "i"))
                    continue;
                if (values.ContainsKey(key))
                    return Refused("repeated-key");
                var reason = Decode(field[(separator + 1)..], out var value);
                if (reason is not null)
                    return Refused(reason);
                values.Add(key, value!);
            }
        }

        if (!values.TryGetValue("x", out var endpoint))
            return Refused("missing-x");
        if (!TryEndpoint(endpoint, out var host, out var port))
            return Refused("bad-x");
        values.TryGetValue("fp", out var fingerprint);
        if (fingerprint is not null && !ValidFingerprint(fingerprint))
            return Refused("bad-fp");
        values.TryGetValue("n", out var name);
        if (name is not null && (name.Length == 0 || name.EnumerateRunes().Count() > MaximumNameScalars
            || name.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.Control
                || IsBidirectionalControl(r))))
            return Refused("bad-n");
        values.TryGetValue("u", out var login);
        if (login is not null && (login.Length is < 3 or > 64
            || login.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))))
            return Refused("bad-u");
        values.TryGetValue("i", out var invitation);
        if (invitation is not null && !ValidInvitation(invitation))
            return Refused("bad-i");
        return new(new(host!, port, fingerprint, name, login?.ToLowerInvariant(), invitation is not null), null);
    }

    /// <summary>
    /// Item BO: a bidirectional formatting control makes a display name read as something else beside the
    /// destination it labels. The zero-width joiners U+200C and U+200D stay allowed - scripts and emoji
    /// sequences need them.
    /// </summary>
    private static bool IsBidirectionalControl(Rune rune) => rune.Value is 0x061C or 0x200E or 0x200F
        or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    /// <summary>Item AR: 22 characters of the URL-safe alphabet that decode to 16 bytes with no stray bits.</summary>
    private static bool ValidInvitation(string value)
    {
        if (value.Length != 22 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return false;
        var standard = value.Replace('-', '+').Replace('_', '/');
        Span<byte> bytes = stackalloc byte[16];
        return Convert.TryFromBase64String(standard + "==", bytes, out var count) && count == bytes.Length
            && Convert.ToBase64String(bytes).TrimEnd('=') == standard;
    }

    /// <summary>Reads at most 4097 bytes, leaves the caller's stream open, and never handles a file path.</summary>
    public static async Task<ExchangeConnectionStringResult> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[MaximumFileBytes + 1];
        var count = 0;
        try
        {
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                count += read;
            }
        }
        catch (IOException)
        {
            return Refused("not-a-string");
        }

        if (count > MaximumFileBytes)
            return Refused("file-too-long");
        string text;
        try
        {
            text = StrictUtf8.GetString(buffer, 0, count);
        }
        catch (DecoderFallbackException)
        {
            return Refused("bad-utf8");
        }

        var trimmed = Trim(text);
        return trimmed.Contains('\r') || trimmed.Contains('\n') ? Refused("file-lines") : Parse(text);
    }

    public static string Encode(ExchangeConnectionString data)
    {
        ArgumentNullException.ThrowIfNull(data);
        const string invalidData = "Invalid DEVICE-EXCHANGE connection data.";
        var host = data.Host?.Contains(':') == true ? $"[{data.Host}]" : data.Host;
        if (!TryEndpoint(host + ":" + data.Port.ToString(CultureInfo.InvariantCulture), out var normalizedHost, out var port))
            throw new ArgumentException(invalidData, nameof(data));
        try
        {
            host = normalizedHost!.Contains(':') ? $"[{normalizedHost}]" : normalizedHost;
            var text = Prefix + "x=" + Escape(host + ":" + port.ToString(CultureInfo.InvariantCulture));
            if (data.Fingerprint is not null) text += "&fp=" + Escape(data.Fingerprint);
            if (data.Name is not null) text += "&n=" + Escape(data.Name);
            if (data.Login is not null) text += "&u=" + Escape(data.Login.ToLowerInvariant());
            if (!Parse(text).Success)
                throw new ArgumentException(invalidData, nameof(data));
            return text;
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(invalidData, nameof(data));
        }
    }

    private static string Trim(string text) => (text.StartsWith('\uFEFF') ? text[1..] : text).Trim();
    private static ExchangeConnectionStringResult Refused(string reason) => new(null, reason);

    private static string? Decode(string encoded, out string? value)
    {
        value = null;
        var buffer = new byte[encoded.Length];
        var count = 0;
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '%')
            {
                if (i + 2 >= encoded.Length || !byte.TryParse(encoded.AsSpan(i + 1, 2),
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var decoded))
                    return "bad-escape";
                buffer[count++] = decoded;
                i += 2;
            }
            else
            {
                buffer[count++] = (byte)(c == '+' ? ' ' : c);
            }
        }

        try
        {
            value = StrictUtf8.GetString(buffer, 0, count);
            return null;
        }
        catch (DecoderFallbackException)
        {
            return "bad-utf8";
        }
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in StrictUtf8.GetBytes(value))
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
                builder.Append((char)b);
            else if (b == ' ')
                builder.Append('+');
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static bool ValidFingerprint(string value)
    {
        if (value.Length != 50 || !value.StartsWith("SHA256:", StringComparison.Ordinal))
            return false;
        Span<byte> hash = stackalloc byte[32];
        return Convert.TryFromBase64String(value[7..] + "=", hash, out var count) && count == hash.Length
            && Convert.ToBase64String(hash).TrimEnd('=') == value[7..];
    }

    private static bool TryEndpoint(string endpoint, out string? host, out int port)
    {
        host = null;
        port = 0;
        var separator = endpoint.LastIndexOf(':');
        if (separator < 1)
            return false;
        var portText = endpoint[(separator + 1)..];
        if (portText.Length is < 1 or > 5 || portText.Any(c => !char.IsAsciiDigit(c))
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            return false;
        var candidate = endpoint[..separator];
        if (candidate.StartsWith('[') && candidate.EndsWith(']'))
        {
            candidate = candidate[1..^1];
            if (candidate.Any(c => c is not (':' or '.') && !char.IsAsciiHexDigit(c))
                || !IPAddress.TryParse(candidate, out var ipv6)
                || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
            host = ipv6.ToString();
            return true;
        }
        if (candidate.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            var parts = candidate.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length is < 1 or > 3 || (p.Length > 1 && p[0] == '0')
                || !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                return false;
        }
        else if (candidate.Length > 253 || candidate.Split('.').Any(label => label.Length is < 1 or > 63
            || label[0] == '-' || label[^1] == '-'
            || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
        {
            return false;
        }

        host = candidate.ToLowerInvariant();
        return true;
    }
}
