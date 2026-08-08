using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0071: reads and writes <c>quality-memory.json</c> beside the catalog state.
///
/// <para>Deliberately <b>not</b> a field of <see cref="CatalogState"/>. That document is the user's
/// catalog - at the observed 19 855 channels it is roughly eleven megabytes, and every save rewrites all
/// of it. A record that changes on a stalling stream must not cost an eleven-megabyte write, and losing
/// this file must cost nothing but one relearned probe, which is not true of anything stored next to the
/// user's channels.</para>
///
/// <para>Every failure is absorbed: an unreadable or absent file reads as no evidence, and a failed write
/// is reported as <c>false</c> rather than thrown. This is a cache with a measured benefit, never a
/// reason for a stream to stop playing.</para>
/// </summary>
public sealed class QualityMemoryStore
{
    private const string StateFileName = "quality-memory.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _directory;
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public QualityMemoryStore(string directory)
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
    public async Task<IReadOnlyList<ChannelQualityMemory>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer
                .DeserializeAsync<List<ChannelQualityMemory>>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces the file atomically - temp file then move - so a crash mid-write leaves the previous
    /// record rather than a truncated one. Returns false when the write did not land.
    /// </summary>
    public async Task<bool> SaveAsync(
        IReadOnlyList<ChannelQualityMemory> entries,
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
            // A stranded temp file is a few kilobytes and the next save overwrites it; failing to delete
            // it must not turn a best-effort cache write into a reported error.
        }
    }
}
