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
/// <para>Every failure is absorbed, with one boundary (SP-0175): a file that could not be read is never
/// written over. An absent, empty or unreadable file reads as no memory - the placement the user already
/// knows - but while the last read failed the store refuses to save, so one closed window cannot replace
/// every remembered placement with a single entry. The refusal reads as <c>false</c>, exactly like any
/// write that did not land, and the next successful load re-enables saving.</para>
/// </remarks>
public sealed class PlayerWindowGeometryStore
{
    private const string StateFileName = "player-geometry.json";
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

    public PlayerWindowGeometryStore(string directory)
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
    public async Task<IReadOnlyList<ChannelWindowGeometry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var (entries, status, _) = await DurableFile
            .ReadAsync<List<ChannelWindowGeometry>>(_path, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        _unreadable = status == FileReadStatus.Unreadable;
        return Sanitize(entries);
    }

    /// <summary>
    /// Replaces the file atomically - temp file then move - so a crash mid-write leaves the previous
    /// placements rather than a truncated one. Returns false when the write did not land, including the
    /// SP-0175 refusal while the file could not be read.
    /// </summary>
    public async Task<bool> SaveAsync(
        IReadOnlyList<ChannelWindowGeometry> entries,
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
    // entries must not throw in recall, placement or record. The other fields are doubles a nonsense
    // value of which PlayerWindowGeometry already degrades to the default placement.
    private static IReadOnlyList<ChannelWindowGeometry> Sanitize(List<ChannelWindowGeometry>? entries) =>
        entries?
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Url))
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
            // A stranded temp file is a few hundred bytes and the next save overwrites it; failing to
            // delete it must not turn a best-effort write into a reported error.
        }
    }
}
