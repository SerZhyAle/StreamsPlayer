using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0084: reads and writes <c>player-geometry.json</c> beside the catalog state.
/// </summary>
/// <remarks>
/// <para>Deliberately <b>not</b> a field of <see cref="CatalogState"/>, for the two reasons the ticket
/// gives as boundaries. That document is the user's catalog - roughly fifteen megabytes at the observed
/// channel count, rewritten whole on every save - and closing a window must not cost that. And a refresh
/// rewrites and removes catalog rows, so a per-channel value living there has to be enumerated in
/// <see cref="UserAuthoredChannels"/> or a refresh silently deletes it; a separate file keyed by URL is
/// simply out of that path, exactly as the quality memory is.</para>
///
/// <para>Every failure is absorbed. An unreadable or absent file reads as no memory, which is the
/// behaviour the user already knows - centred, default size - and a failed write is reported as
/// <c>false</c> rather than thrown. Losing this file costs one window placement, never a channel.</para>
/// </remarks>
public sealed class PlayerWindowGeometryStore
{
    private const string StateFileName = "player-geometry.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _directory;
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public PlayerWindowGeometryStore(string directory)
    {
        _directory = directory;
        _path = Path.Combine(directory, StateFileName);
    }

    /// <summary>The file this store reads and writes. Not named <c>Path</c>: that shadows System.IO.Path.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Everything remembered, or an empty list when the file is absent, empty, or unreadable. A corrupt
    /// file is not repaired and not reported: the next write replaces it wholesale.
    /// </summary>
    public async Task<IReadOnlyList<ChannelWindowGeometry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer
                .DeserializeAsync<List<ChannelWindowGeometry>>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces the file atomically - temp file then move - so a crash mid-write leaves the previous
    /// placements rather than a truncated one. Returns false when the write did not land.
    /// </summary>
    public async Task<bool> SaveAsync(
        IReadOnlyList<ChannelWindowGeometry> entries,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = _path + TemporaryFileExtension;
        try
        {
            Directory.CreateDirectory(_directory);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, entries, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stranded temp file is a few hundred bytes and the next save overwrites it; failing to
            // delete it must not turn a best-effort write into a reported error.
        }
    }
}
