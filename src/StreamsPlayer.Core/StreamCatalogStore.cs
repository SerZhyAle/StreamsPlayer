using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

public sealed class StreamCatalogStore
{
    private const string TemporaryFilePrefix = "catalog-state-";
    private const string TemporaryFileExtension = ".tmp";
    private const string AtlasFilePrefix = "favicon-atlas-";
    private const string AtlasFileExtension = ".png";

    // A stranded temp file is only swept once it is far too old to belong to an in-flight save - including
    // one made by a second running instance, which this process cannot see.
    private static readonly TimeSpan TemporaryFileRetention = TimeSpan.FromHours(1);

    private readonly string _directory;
    private readonly string _statePath;
    // The app saves from many independent UI handlers (scroll debounce, volume, pin, outcome, history).
    // Without this gate, overlapping saves race on File.Move and the loser strands its temp file.
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // TolerantAppLanguageConverter must come first: it is more specific than the general enum
        // converter, and it is what keeps an unreadable language from aborting the whole document.
        Converters = { new TolerantAppLanguageConverter(), new JsonStringEnumConverter() }
    };

    public StreamCatalogStore(string directory)
    {
        _directory = directory;
        _statePath = Path.Combine(directory, "catalog-state.json");
    }

    public string? ResolveAtlasPath(CatalogState state) => state.AtlasFileName is null
        ? null
        : Path.Combine(_directory, state.AtlasFileName);

    public async Task<CatalogState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statePath))
        {
            return new CatalogState();
        }

        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<CatalogState>(stream, _jsonOptions, cancellationToken)
            ?? new CatalogState();
    }

    public async Task<CatalogState> SaveAsync(
        CatalogState state,
        byte[]? newAtlas = null,
        bool replaceAtlas = false,
        CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            return await SaveCoreAsync(state, newAtlas, replaceAtlas, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var committedState = state;
        if (replaceAtlas)
        {
            string? atlasFileName = null;
            if (newAtlas is { Length: > 0 })
            {
                atlasFileName = $"{AtlasFilePrefix}{Guid.NewGuid():N}{AtlasFileExtension}";
                await File.WriteAllBytesAsync(Path.Combine(_directory, atlasFileName), newAtlas, cancellationToken);
            }

            committedState = state with { AtlasFileName = atlasFileName };
        }

        var temporaryPath = Path.Combine(_directory, $"{TemporaryFilePrefix}{Guid.NewGuid():N}{TemporaryFileExtension}");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, committedState, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        catch
        {
            // A cancelled or failed save must not leave a full state serialization behind: at catalog scale
            // that is megabytes per attempt, and nothing else would ever reclaim it.
            TryDelete(temporaryPath);
            throw;
        }

        RemoveUnreferencedFiles(committedState.AtlasFileName);
        return committedState;
    }

    // Runs on every save: drops the atlas the just-saved state no longer names, and any temp file an
    // earlier crash, cancellation, or superseded save stranded.
    private void RemoveUnreferencedFiles(string? currentAtlasFileName)
    {
        var staleBefore = DateTime.UtcNow - TemporaryFileRetention;
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(path);
            if (IsNamed(name, AtlasFilePrefix, AtlasFileExtension))
            {
                if (!name.Equals(currentAtlasFileName, StringComparison.OrdinalIgnoreCase))
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
