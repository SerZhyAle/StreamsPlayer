namespace StreamsPlayer.Core;

/// <summary>
/// SP-0121: the stream a station's playlist link points at. A station published as a <c>.pls</c> or <c>.m3u</c>
/// address answers with a short text list, and the audio engine follows it on its own; the recorder has to do the
/// same, or it saves the list instead of the broadcast.
/// <para>Only the first entry is taken. Every entry of a station playlist is the same broadcast from another
/// server, and the recorder's job is one copy of it - the first is the one the engine plays first too.</para>
/// </summary>
public static class StationPlaylist
{
    /// <summary>The longest playlist body read. Station lists are a few hundred bytes; anything larger is not one.</summary>
    public const int MaximumBodyBytes = 64 * 1024;

    /// <summary>How many playlist hops the recorder follows before giving up - a list may point at a list.</summary>
    public const int MaximumHops = 3;

    /// <summary>True when the text reads as a PLS or M3U body.</summary>
    public static bool LooksLikePlaylist(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
            || FirstAddressLine(trimmed) is not null;
    }

    /// <summary>
    /// The first playable address in a PLS (<c>File1=</c>) or M3U body, resolved against the playlist's own
    /// address when relative. Null when the body names nothing an http(s) client can open.
    /// </summary>
    public static Uri? FirstStream(string body, Uri playlistAddress)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            string candidate;
            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                // PLS: only FileN= entries are addresses; TitleN=, LengthN=, NumberOfEntries= are not.
                var key = line[..equals].Trim();
                if (!key.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidate = line[(equals + 1)..].Trim();
            }
            else
            {
                candidate = line;
            }

            if (Resolve(candidate, playlistAddress) is { } stream)
            {
                return stream;
            }
        }

        return null;
    }

    private static string? FirstAddressLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            return line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? line
                : null;
        }

        return null;
    }

    private static Uri? Resolve(string candidate, Uri playlistAddress)
    {
        if (!Uri.TryCreate(playlistAddress, candidate, out var resolved))
        {
            return null;
        }

        return resolved.Scheme is "http" or "https" ? resolved : null;
    }
}
