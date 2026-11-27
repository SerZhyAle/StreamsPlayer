using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// STRWIN-0303: the three small documents of the remote-control target, each beside the catalog state and outside it
/// (the reasons of the local-state split: a save of one never rewrites the catalog, and losing one costs only what it
/// remembered). <c>remote-control.json</c> holds the person's switch and grants, <c>remote-command-ring.json</c> the ring of
/// rule 12, <c>remote-command-log.json</c> the log of rule 11. A document that cannot be read reads as the safe value -
/// off, an empty ring, an empty log - and is not saved over until a read succeeded (SP-0175).
/// </summary>
public sealed class RemoteControlStore
{
    public const string SettingsFileName = "remote-control.json";
    public const string RingFileName = "remote-command-ring.json";
    public const string LogFileName = "remote-command-log.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    public RemoteControlStore(string directory) => _directory = directory;

    public async Task<RemoteControlSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync<RemoteControlSettings>(SettingsFileName, cancellationToken).ConfigureAwait(false))?.Sanitized()
        ?? RemoteControlSettings.Off;

    public Task<bool> SaveSettingsAsync(RemoteControlSettings settings, CancellationToken cancellationToken = default) =>
        WriteAsync(SettingsFileName, settings, cancellationToken);

    public async Task<RemoteCommandRing> LoadRingAsync(CancellationToken cancellationToken = default) =>
        new(await ReadAsync<List<RemoteRingEntry>>(RingFileName, cancellationToken).ConfigureAwait(false));

    public Task<bool> SaveRingAsync(RemoteCommandRing ring, CancellationToken cancellationToken = default) =>
        WriteAsync(RingFileName, ring.Entries, cancellationToken);

    public async Task<RemoteCommandLog> LoadLogAsync(CancellationToken cancellationToken = default) =>
        new(await ReadAsync<List<RemoteCommandLogEntry>>(LogFileName, cancellationToken).ConfigureAwait(false));

    public Task<bool> SaveLogAsync(RemoteCommandLog log, CancellationToken cancellationToken = default) =>
        WriteAsync(LogFileName, log.Entries, cancellationToken);

    private async Task<T?> ReadAsync<T>(string fileName, CancellationToken cancellationToken) where T : class
    {
        var (value, status, _) = await DurableFile
            .ReadAsync<T>(Path.Combine(_directory, fileName), _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        lock (_unreadable)
        {
            if (status == FileReadStatus.Unreadable)
            {
                _unreadable.Add(fileName);
            }
            else
            {
                _unreadable.Remove(fileName);
            }
        }

        return value;
    }

    private async Task<bool> WriteAsync<T>(string fileName, T value, CancellationToken cancellationToken)
    {
        lock (_unreadable)
        {
            if (_unreadable.Contains(fileName))
            {
                return false;
            }
        }

        var path = Path.Combine(_directory, fileName);
        var temporaryPath = path + TemporaryFileExtension;
        try
        {
            Directory.CreateDirectory(_directory);
            await DurableFile.ReplaceAsync(path, temporaryPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream, value, _jsonOptions, token),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A stranded temp file is overwritten by the next save.
            }

            return false;
        }
    }
}
