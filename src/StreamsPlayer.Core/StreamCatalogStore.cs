using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamsPlayer.Core;

public sealed class StreamCatalogStore
{
    private readonly string _directory;
    private readonly string _statePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
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
        Directory.CreateDirectory(_directory);
        var committedState = state;
        if (replaceAtlas)
        {
            string? atlasFileName = null;
            if (newAtlas is { Length: > 0 })
            {
                atlasFileName = $"favicon-atlas-{Guid.NewGuid():N}.png";
                await File.WriteAllBytesAsync(Path.Combine(_directory, atlasFileName), newAtlas, cancellationToken);
            }

            committedState = state with { AtlasFileName = atlasFileName };
        }

        var temporaryPath = Path.Combine(_directory, $"catalog-state-{Guid.NewGuid():N}.tmp");
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, committedState, _jsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _statePath, overwrite: true);
        RemoveUnreferencedAtlases(committedState.AtlasFileName);
        return committedState;
    }

    private void RemoveUnreferencedAtlases(string? currentFileName)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "favicon-atlas-*.png"))
        {
            if (!Path.GetFileName(path).Equals(currentFileName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // It is safe to retry cleanup on a future save.
                }
            }
        }
    }
}
