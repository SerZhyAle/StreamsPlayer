using System.Text;

namespace StreamsPlayer.Core;

/// <summary>
/// Fetches a remote M3U playlist over HTTP(S) and decodes both remote and local bytes with the same strict
/// UTF-8 rule the import contract requires. Mirrors <see cref="StreamCatalogService"/>: an injected
/// <see cref="HttpClient"/>, a 30-second deadline, and header-first streaming.
/// </summary>
public sealed class M3uImportService
{
    private const char ByteOrderMark = '﻿';
    private readonly HttpClient _httpClient;

    public M3uImportService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        return DecodeUtf8(bytes);
    }

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
