using System.Net.Http.Headers;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Fetches a remote M3U playlist over HTTP(S) and decodes both remote and local bytes with the same strict
/// UTF-8 rule the import contract requires.
/// </summary>
/// <remarks>
/// SP-0129: bounded the way every other download is - a deadline on the response head, a silence bound on
/// the body, and a ceiling. It used to buffer the whole body under one 30-second wall clock, so a pasted
/// link to a video file or a live stream pulled hundreds of megabytes before failing; such a response is
/// now refused from its head, before any of the body is read.
/// </remarks>
public sealed class M3uImportService
{
    /// <summary>
    /// Largest playlist accepted. A read ceiling, not a size: a radio playlist is kilobytes, and the large
    /// public IPTV lists (tens of thousands of entries) are a few megabytes, so this keeps an order of
    /// magnitude of headroom over the largest real playlist while refusing a media file long before it
    /// could matter.
    /// </summary>
    public const long MaximumPlaylistBytes = 32L * 1024 * 1024;

    public static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(20);

    private const char ByteOrderMark = '﻿';
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _idleTimeout;

    public M3uImportService(HttpClient httpClient)
        : this(httpClient, DefaultHeaderTimeout, DefaultIdleTimeout)
    {
    }

    /// <summary>Tests shorten the bounds; the product always uses the defaults.</summary>
    internal M3uImportService(HttpClient httpClient, TimeSpan headerTimeout, TimeSpan idleTimeout)
    {
        _httpClient = httpClient;
        _headerTimeout = headerTimeout;
        _idleTimeout = idleTimeout;
    }

    /// <exception cref="HttpRequestException">The server refused or could not be reached.</exception>
    /// <exception cref="InvalidDataException">
    /// The response is media rather than a playlist, or larger than <see cref="MaximumPlaylistBytes"/>.
    /// </exception>
    /// <exception cref="TimeoutException">No head within the header bound, or the body went silent.</exception>
    /// <exception cref="DecoderFallbackException">The body is not valid UTF-8.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await HttpDownload.SendForHeadersAsync(_httpClient, request, _headerTimeout, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (DescribesMedia(response))
        {
            throw new InvalidDataException(
                $"The address serves media ({response.Content.Headers.ContentType?.MediaType ?? "a live stream"}), not a playlist.");
        }

        var bytes = await HttpDownload.ReadAllBytesAsync(
            response, progress: null, MaximumPlaylistBytes, _idleTimeout, cancellationToken);
        return DecodeUtf8(bytes);
    }

    /// <summary>
    /// True when the head alone shows the body is a media stream rather than a playlist: an Icecast or
    /// Shoutcast station (it announces itself with <c>icy-*</c> headers), or an <c>audio/*</c> or
    /// <c>video/*</c> type that is not one of the playlist types those families also register.
    /// </summary>
    /// <remarks>
    /// Only positive evidence refuses. A missing or generic type (<c>text/plain</c>,
    /// <c>application/octet-stream</c>) is common for real playlists on static hosting, so those are read
    /// and left to the ceiling and the parser.
    /// </remarks>
    internal static bool DescribesMedia(HttpResponseMessage response)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.StartsWith("icy-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return DescribesMedia(response.Content.Headers.ContentType);
    }

    internal static bool DescribesMedia(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        if (mediaType is null)
        {
            return false;
        }

        var isMediaFamily = mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                            || mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        return isMediaFamily && !IsPlaylistType(mediaType);
    }

    // audio/x-mpegurl, audio/mpegurl, vnd.apple.mpegurl (M3U and HLS), audio/x-scpls (PLS), and the
    // x-ms-asf / x-ms-wax / x-ms-wvx redirector lists: all text, all things a station link can return.
    private static bool IsPlaylistType(string mediaType) =>
        mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("scpls", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("x-ms-asf", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("x-ms-wax", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("x-ms-wvx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes bytes as strict UTF-8, stripping a leading BOM. Invalid UTF-8 throws
    /// <see cref="DecoderFallbackException"/>, which the caller reports as an invalid-encoding import that
    /// leaves state unchanged.
    /// </summary>
    public static string DecodeUtf8(byte[] bytes)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        return text.Length > 0 && text[0] == ByteOrderMark ? text[1..] : text;
    }
}
