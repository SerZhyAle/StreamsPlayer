namespace StreamPlayer.Core;

public sealed class StreamCatalogService
{
    public const string CatalogUrl =
        "https://github.com/SerZhyAle/FastMediaSorter_mob_v2/releases/download/delivery-so-v1/stream-catalog.zip";

    private readonly HttpClient _httpClient;
    private readonly StreamCatalogStore _store;

    public StreamCatalogService(HttpClient httpClient, StreamCatalogStore store)
    {
        _httpClient = httpClient;
        _store = store;
    }

    public async Task<CatalogRefreshResult> RefreshAsync(
        CatalogState currentState,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        using var stream = new MemoryStream(bytes, writable: false);
        var bank = StreamBankReader.Read(stream);
        if (bank.Entries.Count == 0)
        {
            throw new InvalidDataException("The downloaded catalog contains no valid channels.");
        }

        var now = DateTimeOffset.UtcNow;
        var merge = CatalogMerger.Merge(currentState.Channels, bank.Entries, now);
        var state = currentState with
        {
            Channels = merge.Channels.ToList(),
            LastCatalogRefreshAt = now
        };
        state = await _store.SaveAsync(state, bank.FaviconAtlas, replaceAtlas: true, cancellationToken);
        return new CatalogRefreshResult(state, merge.Added, merge.Updated, merge.Removed);
    }
}
