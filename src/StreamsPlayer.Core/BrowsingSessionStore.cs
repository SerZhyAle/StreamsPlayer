using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads and writes <see cref="BrowsingSession"/> in its own small file (SP-0067), so the paths that
/// change constantly - a scroll coming to rest, a card click - stop rewriting the channel catalog.
/// </summary>
public sealed class BrowsingSessionStore
{
    // Deliberately not "catalog-state-": StreamCatalogStore.RemoveUnreferencedFiles sweeps that prefix
    // on every catalog save and would delete a session temp file out from under an in-flight write.
    private const string TemporaryFilePrefix = "browsing-session-";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _directory;
    private readonly string _sessionPath;

    // The same reason StreamCatalogStore has one: saves are raised from independent UI handlers and
    // overlapping ones would race on File.Move, stranding the loser's temp file.
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    // WriteIndented is off, unlike the catalog store's historical setting: this file is machine state
    // that is rewritten several times a minute, and nothing ever reads it by eye.
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public BrowsingSessionStore(string directory)
    {
        _directory = directory;
        _sessionPath = Path.Combine(directory, "browsing-session.json");
    }

    /// <summary>The session file this store reads and writes.</summary>
    public string SessionPath => _sessionPath;

    /// <summary>
    /// Loads the session, migrating once from <paramref name="migrationSource"/> when this store has no
    /// file of its own.
    /// </summary>
    /// <remarks>
    /// The migration is one-time by construction: it writes the session file as part of the same call,
    /// so the next load finds it and never looks at <paramref name="migrationSource"/> again. There is
    /// deliberately no permanent dual-read path (SP-0067 settled question 3) - the old fields stay on
    /// <see cref="CatalogState"/> so an existing file keeps loading, and stop being written.
    /// <para>
    /// <see cref="CatalogState.CatalogScrollAnchorId"/> has no counterpart here: it named a channel and
    /// the session stores a position. It migrates to <c>ScrollOffset = 0</c>, which restores the top of
    /// the list rather than inventing a place the user never was.
    /// </para>
    /// </remarks>
    public async Task<BrowsingSession> LoadAsync(
        CatalogState migrationSource,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(_sessionPath))
        {
            try
            {
                await using var stream = File.OpenRead(_sessionPath);
                return await JsonSerializer
                    .DeserializeAsync<BrowsingSession>(stream, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false) ?? new BrowsingSession();
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // Losing the saved filters is recoverable and invisible after one interaction; failing
                // the launch over them is not. The file is left in place for diagnosis and overwritten
                // by the next save.
                return new BrowsingSession();
            }
        }

        var migrated = new BrowsingSession
        {
            SearchQuery = migrationSource.CatalogSearchQuery,
            MediaFilter = migrationSource.CatalogMediaFilter,
            CategoryFilter = migrationSource.CatalogCategoryFilter,
            TopicFilter = migrationSource.CatalogTopicFilter,
            LanguageFilter = migrationSource.CatalogLanguageFilter,
            CountryFilter = migrationSource.CatalogCountryFilter,
            MinBitrateFilter = migrationSource.CatalogMinBitrateFilter,
            CollectionFilter = migrationSource.CatalogCollectionFilter,
            SortMode = migrationSource.CatalogSortMode,
            ScrollOffset = 0,
            LastSelectedChannelId = migrationSource.LastSelectedChannelId
        };
        await SaveAsync(migrated, cancellationToken).ConfigureAwait(false);
        return migrated;
    }

    public async Task SaveAsync(BrowsingSession session, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveCoreAsync(BrowsingSession session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(
            _directory, $"{TemporaryFilePrefix}{Guid.NewGuid():N}{TemporaryFileExtension}");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer
                    .SerializeAsync(stream, session, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _sessionPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        RemoveStrandedTemporaryFiles();
    }

    // Only this store's own temp files, and only ones far too old to belong to an in-flight save -
    // including one left by a second running instance, which this process cannot see. It must never
    // touch catalog-state files: the two sweeps share a directory and each owns exactly its own prefix.
    private void RemoveStrandedTemporaryFiles()
    {
        var staleBefore = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var path in Directory.EnumerateFiles(_directory, $"{TemporaryFilePrefix}*{TemporaryFileExtension}"))
        {
            if (File.GetLastWriteTimeUtc(path) < staleBefore)
            {
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort and must never fail the save that triggered it.
        }
    }
}
