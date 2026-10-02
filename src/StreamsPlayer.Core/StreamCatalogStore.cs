using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

public sealed class StreamCatalogStore
{
    private const string TemporaryFilePrefix = "catalog-state-";
    private const string TemporaryFileExtension = ".tmp";
    private const string AtlasFilePrefix = "favicon-atlas-";
    private const string SnapshotAtlasFilePrefix = "snapshot-atlas-";
    private const string ImportedAtlasFilePrefix = "imported-atlas-";
    private const string AtlasFileExtension = ".png";

    // SP-0125: what the FaviconSource converter reads a value it does not know as. Deliberately not a
    // member of the enum - it exists only between deserialization and NormalizeUnreadableFaviconSources,
    // so no other code can ever see it and it is never written back.
    private const FaviconSource UnreadableFaviconSource = (FaviconSource)(-1);

    // A stranded temp file is only swept once it is far too old to belong to an in-flight save - including
    // one made by a second running instance, which this process cannot see.
    private static readonly TimeSpan TemporaryFileRetention = TimeSpan.FromHours(1);

    private readonly string _directory;
    private readonly string _statePath;
    // Set by the last load; read by every save. Volatile: the load runs once on the UI thread at
    // start-up, while saves run under the gate on pool threads.
    private volatile bool _stateUnreadable;

    // SP-0175: one wording for both halves of the rule - the load that failed and the save it forbids.
    private const string UnreadableStateMessage =
        "The catalog state file could not be read, so it is preserved untouched until a read succeeds (SP-0175).";
    // The app saves from many independent UI handlers (scroll debounce, volume, pin, outcome, history).
    // Without this gate, overlapping saves race on File.Move and the loser strands its temp file.
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        // SP-0067: off. This is machine state, not a document - nothing reads it by eye, and at 19 855
        // channels the indentation was about a third of a 15 MB file, paid on every save.
        WriteIndented = false,
        // SP-0035: every enum the state persists reads through a tolerant converter, so a value written
        // by a newer build costs that one field instead of the entire document (and with it the user's
        // catalog, collections and history). Order matters - the specific converters must precede the
        // general JsonStringEnumConverter, which is what still throws for anything not listed here.
        // Each fallback is the value a fresh install would hold, except the two that protect data:
        // an unknown SourceOrigin reads as Manual, the origin a catalog refresh never rewrites or
        // prunes, and an unknown MediaKind reads as Video, whose player also handles audio and RTSP.
        // LastPlayOutcome is optional, so "unreadable" has an honest representation there - absent -
        // and inventing a recorded failure would be a claim the state does not support.
        Converters =
        {
            new TolerantAppLanguageConverter(),
            new TolerantEnumConverter<AppTheme>(AppTheme.System),
            new TolerantEnumConverter<CatalogViewMode>(CatalogViewMode.List),
            new TolerantEnumConverter<StreamTileSize>(StreamTileSize.Medium),
            new TolerantEnumConverter<MediaBackend>(MediaBackend.LibVlc),
            new TolerantEnumConverter<AudioChannelMode>(AudioChannelMode.Stereo),
            new TolerantEnumConverter<ChannelAccess>(ChannelAccess.Open),
            new TolerantEnumConverter<MediaKind>(MediaKind.Video),
            new TolerantEnumConverter<SourceOrigin>(SourceOrigin.Manual),
            // SP-0052 / SP-0125: an unreadable atlas ownership says nothing about which sheet the row's
            // index belongs to, and resolved against any installed sheet it would show another channel's
            // icon. It reads as a sentinel that the load turns into Catalog with no index - the monogram.
            new TolerantEnumConverter<FaviconSource>(UnreadableFaviconSource),
            new TolerantNullableEnumConverter<PlayOutcome>(),
            new JsonStringEnumConverter()
        }
    };

    public StreamCatalogStore(string directory)
    {
        _directory = directory;
        _statePath = Path.Combine(directory, "catalog-state.json");
    }

    /// <summary>
    /// The state file this store reads and writes. Exposed for SP-0067's measurement, which reports the
    /// size and the last-write time of the file a browsing-session save used to rewrite in full.
    /// </summary>
    public string StatePath => _statePath;

    /// <summary>
    /// Whether this machine has run the product before. SP-0059 asks the first-launch question only
    /// when it has not, and this must be read <em>before</em> <see cref="LoadAsync"/>: that same launch
    /// persists the detected interface language, so every moment after it looks like a used machine.
    /// </summary>
    public bool HasStoredState => File.Exists(_statePath);

    public string? ResolveAtlasPath(CatalogState state, AtlasSlot slot = AtlasSlot.Catalog)
    {
        var fileName = FileNameOf(state, slot);
        return fileName is null ? null : Path.Combine(_directory, fileName);
    }

    private static string? FileNameOf(CatalogState state, AtlasSlot slot) => slot switch
    {
        AtlasSlot.Snapshot => state.SnapshotAtlasFileName,
        AtlasSlot.Imported => state.ImportedAtlasFileName,
        _ => state.AtlasFileName
    };

    private static string PrefixOf(AtlasSlot slot) => slot switch
    {
        AtlasSlot.Snapshot => SnapshotAtlasFilePrefix,
        AtlasSlot.Imported => ImportedAtlasFilePrefix,
        _ => AtlasFilePrefix
    };

    public async Task<CatalogState> LoadAsync(CancellationToken cancellationToken = default)
    {
        // SP-0067: ConfigureAwait(false) throughout the load and the save. Deserializing 15 MB and
        // serializing it back are the two longest awaits in the application, and resuming them on the
        // Dispatcher put that continuation in front of the user's next keystroke for no reason - none of
        // this touches UI state.
        var (state, status, failure) = await DurableFile
            .ReadAsync<CatalogState>(_statePath, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (status == FileReadStatus.Unreadable)
        {
            // SP-0175: the file's content is unknown, so nothing may save over it - the store refuses
            // every save until a read has succeeded, which in practice is the restart the start-up
            // notice already asks for. The real cause travels inside for the log.
            _stateUnreadable = true;
            throw new IOException(UnreadableStateMessage, failure);
        }

        _stateUnreadable = false;
        return NormalizeUnreadableFaviconSources(state ?? new CatalogState());
    }

    private static CatalogState NormalizeUnreadableFaviconSources(CatalogState state)
    {
        if (!state.Channels.Any(channel => channel.FaviconSource == UnreadableFaviconSource))
        {
            return state;
        }

        return state with
        {
            Channels = [.. state.Channels.Select(channel => channel.FaviconSource == UnreadableFaviconSource
                ? channel with { FaviconSource = FaviconSource.Catalog, FaviconIndex = null }
                : channel)]
        };
    }

    public async Task<CatalogState> SaveAsync(
        CatalogState state,
        byte[]? newAtlas = null,
        bool replaceAtlas = false,
        CancellationToken cancellationToken = default) =>
        await SaveAsync(state, newAtlas, replaceAtlas, AtlasSlot.Catalog, cancellationToken);

    public async Task<CatalogState> SaveAsync(
        CatalogState state,
        byte[]? newAtlas,
        bool replaceAtlas,
        AtlasSlot slot,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SaveCoreAsync(state, newAtlas, replaceAtlas, slot, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<CatalogState> SaveCoreAsync(
        CatalogState state,
        byte[]? newAtlas,
        bool replaceAtlas,
        AtlasSlot slot,
        CancellationToken cancellationToken)
    {
        if (_stateUnreadable)
        {
            // SP-0175: never replace a file whose content this session never managed to read. The
            // committer reports this as a failed save with a pending retry, which stays failed until the
            // restart the load notice asks for.
            throw new IOException(UnreadableStateMessage);
        }

        Directory.CreateDirectory(_directory);
        var committedState = state;
        if (replaceAtlas)
        {
            string? atlasFileName = null;
            if (newAtlas is { Length: > 0 })
            {
                atlasFileName = $"{PrefixOf(slot)}{Guid.NewGuid():N}{AtlasFileExtension}";
                await File.WriteAllBytesAsync(Path.Combine(_directory, atlasFileName), newAtlas, cancellationToken)
                    .ConfigureAwait(false);
            }

            // SP-0052 & SP-0098: a save writes exactly one slot. The other slots' file names are carried through
            // untouched, so replacing one atlas never strands or deletes the others.
            committedState = slot switch
            {
                AtlasSlot.Snapshot => state with { SnapshotAtlasFileName = atlasFileName },
                AtlasSlot.Imported => state with { ImportedAtlasFileName = atlasFileName },
                _ => state with { AtlasFileName = atlasFileName }
            };
        }

        var temporaryPath = Path.Combine(_directory, $"{TemporaryFilePrefix}{Guid.NewGuid():N}{TemporaryFileExtension}");
        try
        {
            await DurableFile.ReplaceAsync(
                _statePath,
                temporaryPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream, committedState, _jsonOptions, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A cancelled or failed save must not leave a full state serialization behind: at catalog scale
            // that is megabytes per attempt, and nothing else would ever reclaim it.
            TryDelete(temporaryPath);
            throw;
        }

        RemoveUnreferencedFiles(committedState);
        return committedState;
    }

    // Runs on every save: drops every atlas the just-saved state no longer names in any slot, and any
    // temp file an earlier crash, cancellation, or superseded save stranded. All slots are checked on
    // every save, whichever one was written - a save of one slot must not sweep another.
    private void RemoveUnreferencedFiles(CatalogState committedState)
    {
        try
        {
            var staleBefore = DateTime.UtcNow - TemporaryFileRetention;
            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                var name = Path.GetFileName(path);
                if (IsNamed(name, AtlasFilePrefix, AtlasFileExtension) ||
                    IsNamed(name, SnapshotAtlasFilePrefix, AtlasFileExtension) ||
                    IsNamed(name, ImportedAtlasFilePrefix, AtlasFileExtension))
                {
                    if (!name.Equals(committedState.AtlasFileName, StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(committedState.SnapshotAtlasFileName, StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(committedState.ImportedAtlasFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(path);
                    }
                }
                else if (IsNamed(name, TemporaryFilePrefix, TemporaryFileExtension) &&
                    File.GetLastWriteTimeUtc(path) < staleBefore)
                {
                    TryDelete(path);
                }
            }
        }
        // The state replacement already succeeded. A cleanup failure may leave an orphan file, but
        // reporting the save as failed here would leave the committer's memory behind the disk.
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
    }

    private static bool IsNamed(string name, string prefix, string extension) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held by an in-flight save (FileShare.None) or otherwise locked; a later save retries.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best-effort and must never fail the save that triggered it.
        }
    }
}
