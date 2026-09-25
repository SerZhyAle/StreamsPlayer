using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0075: <c>tv-schedule.json</c> and <c>tv-schedule-bindings.json</c> beside the catalog state.
///
/// <para>Deliberately not fields of <see cref="CatalogState"/>: the schedule is external data that is
/// replaced wholesale on every download, and the user's bindings are keyed by URL beside it - the same
/// split SP-0071 made for quality memory. Neither file can reach a catalog row, so a refresh, a merge or
/// a corrupt schedule never touches a MANUAL or IMPORTED channel.</para>
///
/// <para>Reads are tolerant: an absent, unreadable or newer-schema file reads as "no schedule" / "no
/// bindings". Writes are atomic (temp file then move) and report failure as <c>false</c>.</para>
/// </summary>
public sealed class TvScheduleStore
{
    public const string ScheduleFileName = "tv-schedule.json";
    public const string BindingsFileName = "tv-schedule-bindings.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public TvScheduleStore(string directory)
    {
        _directory = directory;
    }

    public string SchedulePath => Path.Combine(_directory, ScheduleFileName);
    public string BindingsPath => Path.Combine(_directory, BindingsFileName);

    public async Task<TvScheduleDocument?> LoadScheduleAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync<TvScheduleDocument>(SchedulePath, cancellationToken).ConfigureAwait(false);
        return document is { SchemaVersion: TvScheduleDocument.CurrentSchemaVersion, Channels: not null }
            ? document
            : null;
    }

    public async Task<IReadOnlyList<TvScheduleBinding>> LoadBindingsAsync(CancellationToken cancellationToken = default)
    {
        var bindings = await ReadAsync<List<TvScheduleBinding>>(BindingsPath, cancellationToken).ConfigureAwait(false);
        return bindings?.Where(binding => !string.IsNullOrWhiteSpace(binding?.Url)).ToList() ?? [];
    }

    public Task<bool> SaveScheduleAsync(TvScheduleDocument document, CancellationToken cancellationToken = default) =>
        WriteAsync(SchedulePath, document, cancellationToken);

    public Task<bool> SaveBindingsAsync(IReadOnlyList<TvScheduleBinding> bindings, CancellationToken cancellationToken = default) =>
        WriteAsync(BindingsPath, bindings, cancellationToken);

    /// <summary>Removes both files. Returns the number of files that existed and are now gone.</summary>
    /// <exception cref="IOException">A file exists and could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file exists and could not be deleted.</exception>
    public int Delete()
    {
        var removed = 0;
        foreach (var path in new[] { SchedulePath, BindingsPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                removed++;
            }
        }

        return removed;
    }

    public bool HasAnyFile => File.Exists(SchedulePath) || File.Exists(BindingsPath);

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<bool> WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + TemporaryFileExtension;
        try
        {
            Directory.CreateDirectory(_directory);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stranded temp file is overwritten by the next save; it must not turn a failed write into a crash.
        }
    }
}
