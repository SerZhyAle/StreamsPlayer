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
/// <para>Every failure is absorbed, with one boundary (SP-0175): a file that could not be read is never
/// written over. An absent, empty or unreadable file reads as no evidence, but while the last read failed
/// the store refuses to save - overwriting what it could not see would destroy every other channel's
/// record over a transient lock. The refusal reads as <c>false</c>, exactly like any write that did not
/// land, and the next successful load re-enables saving.</para>
/// </summary>
public sealed class QualityMemoryStore
{
    private const string StateFileName = "quality-memory.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _directory;
    private readonly string _path;
    // Set by the last load; read by every save. Volatile: loads and saves are serialized by the App-side
    // gate, but nothing in this class enforces that ordering.
    private volatile bool _unreadable;
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
    /// file is left untouched on disk; the store refuses writes until a read has succeeded (SP-0175).
    /// </summary>
    public async Task<IReadOnlyList<ChannelQualityMemory>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var (entries, status, _) = await DurableFile
            .ReadAsync<List<ChannelQualityMemory>>(_path, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        _unreadable = status == FileReadStatus.Unreadable;
        return Sanitize(entries);
    }

    /// <summary>
    /// Replaces the file atomically - temp file then move - so a crash mid-write leaves the previous
    /// record rather than a truncated one. Returns false when the write did not land, including the
    /// SP-0175 refusal while the file could not be read.
    /// </summary>
    public async Task<bool> SaveAsync(
        IReadOnlyList<ChannelQualityMemory> entries,
        CancellationToken cancellationToken = default)
    {
        if (_unreadable)
        {
            return false;
        }

        var temporaryPath = _path + TemporaryFileExtension;
        try
        {
            Directory.CreateDirectory(_directory);
            await DurableFile.ReplaceAsync(
                _path,
                temporaryPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream, entries, _jsonOptions, token),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    // SP-0175: the file is hand-editable and outlives builds, so a document that parses but holds null
    // entries or null rungs must not throw in recall or record. An invalid piece is dropped; a record
    // whose rungs are unusable keeps its ceiling, which is still real evidence.
    private static IReadOnlyList<ChannelQualityMemory> Sanitize(List<ChannelQualityMemory>? entries) =>
        entries?
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Url))
            .Select(entry => entry with
            {
                Rungs = entry.Rungs?.Where(rung => rung is not null).ToList() ?? []
            })
            .ToList()
        ?? [];

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
