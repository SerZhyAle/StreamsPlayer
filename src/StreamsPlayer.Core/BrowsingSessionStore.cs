using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// Reads and writes <see cref="BrowsingSession"/> in its own small file (SP-0067), so the paths that
/// change constantly - a scroll coming to rest, a card click - stop rewriting the channel catalog.
/// SP-0175: a session file that could not be read is never written over, and a migration save that
/// fails defers to the next launch instead of failing the load.
/// </summary>
public sealed class BrowsingSessionStore
{
    // Deliberately not "catalog-state-": StreamCatalogStore.RemoveUnreferencedFiles sweeps that prefix
    // on every catalog save and would delete a session temp file out from under an in-flight write.
    private const string TemporaryFilePrefix = "browsing-session-";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _directory;
    private readonly string _sessionPath;

    // Set by the last load; read by every save. Volatile: the load runs once on the UI thread at
    // start-up, while saves run under the gate on pool threads.
    private volatile bool _sessionUnreadable;

    // SP-0175: one wording for both halves of the rule - the load that failed and the save it forbids.
    private const string UnreadableSessionMessage =
        "The browsing-session file could not be read, so it is preserved untouched until a read succeeds (SP-0175).";

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
    /// so the next load finds it and never looks at <paramref name="migrationSource"/> again - and when
    /// that write fails, the file is still absent, so the next launch simply migrates again (SP-0175).
    /// There is deliberately no permanent dual-read path (SP-0067 settled question 3) - the old fields
    /// stay on <see cref="CatalogState"/> so an existing file keeps loading, and stop being written.
    /// <para>
    /// <see cref="CatalogState.CatalogScrollAnchorId"/> has no counterpart here: it named a channel and
    /// the session stores a position. It migrates to <c>ScrollOffset = 0</c>, which restores the top of
    /// the list rather than inventing a place the user never was.
    /// </para>
    /// </remarks>
    public async Task<BrowsingSession> LoadAsync(
        CatalogState migrationSource,
        CancellationToken cancellationToken = default,
        Action<Exception>? onMigrationSaveFailure = null)
    {
        var (session, status, _) = await DurableFile
            .ReadAsync<BrowsingSession>(_sessionPath, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (status == FileReadStatus.Read)
        {
            _sessionUnreadable = false;
            return session ?? new BrowsingSession();
        }

        if (status == FileReadStatus.Unreadable)
        {
            // SP-0175: the file is preserved untouched, and saves are refused until a read has
            // succeeded, so a scroll position cannot cost the filters the file still holds.
            _sessionUnreadable = true;
            return new BrowsingSession();
        }

        _sessionUnreadable = false;
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
        try
        {
            await SaveAsync(migrated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // SP-0175: a migration save that fails - a locked or unwritable state folder - must not fail
            // the load it is part of, which for two releases read as a catalog load failure (SP-0116
            // A-18). The session file is still absent, so the next launch migrates again from the same
            // legacy fields; the callback lets the caller log the deferral.
            onMigrationSaveFailure?.Invoke(exception);
        }

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
        if (_sessionUnreadable)
        {
            // SP-0175: never replace a file whose content this session never managed to read. The
            // caller already absorbs this the way it absorbs any failed local write.
            throw new IOException(UnreadableSessionMessage);
        }

        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(
            _directory, $"{TemporaryFilePrefix}{Guid.NewGuid():N}{TemporaryFileExtension}");
        try
        {
            await DurableFile.ReplaceAsync(
                _sessionPath,
                temporaryPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream, session, _jsonOptions, token),
                cancellationToken).ConfigureAwait(false);
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
