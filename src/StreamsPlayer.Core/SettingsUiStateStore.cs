using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0191: reads and writes <c>settings-ui.json</c> beside the catalog state - the settings
/// window's private navigation context (<see cref="SettingsUiState"/>).
/// </summary>
/// <remarks>
/// Same shape and boundaries as <see cref="PlayerWindowGeometryStore"/>: a small sibling document
/// kept out of <see cref="CatalogState"/> on purpose, written atomically (temp file then move), and
/// a file that could not be read is never written over - an absent or unreadable file reads as no
/// memory, and while the last read failed the store refuses saves, so one window cannot replace
/// everything remembered with a single entry.
/// </remarks>
public sealed class SettingsUiStateStore
{
    private const string StateFileName = "settings-ui.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _path;
    private volatile bool _unreadable;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public SettingsUiStateStore(string directory) =>
        _path = Path.Combine(directory, StateFileName);

    /// <summary>The file this store reads and writes. Not named <c>Path</c>: that shadows System.IO.Path.</summary>
    public string FilePath => _path;

    /// <summary>What is remembered, or an empty state when the file is absent, empty or unreadable.</summary>
    public async Task<SettingsUiState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var (state, status, _) = await DurableFile
            .ReadAsync<SettingsUiState>(_path, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        _unreadable = status == FileReadStatus.Unreadable;
        return Sanitize(state);
    }

    /// <summary>
    /// The synchronous read the settings constructor uses. The initial page, the group expansion and
    /// the window placement all have to be decided before the window is shown, and a constructor
    /// cannot await; the file is a few kilobytes beside the catalog state, so the read is the cost of
    /// one small disk hit, not of a document rewrite. Same refusal as <see cref="SaveAsync"/>: a read
    /// that failed marks the store, and the next save declines until a read succeeds.
    /// </summary>
    public SettingsUiState LoadSync()
    {
        try
        {
            // A zero-byte file holds nothing to lose, the same as an absent one (FileReadStatus.Absent): the
            // async read says so, and treating it as unparseable here would refuse every save for good.
            if (!File.Exists(_path) || new FileInfo(_path).Length == 0)
            {
                _unreadable = false;
                return new SettingsUiState();
            }

            var state = JsonSerializer.Deserialize<SettingsUiState>(File.ReadAllText(_path), _jsonOptions);
            _unreadable = false;
            return Sanitize(state);
        }
        catch
        {
            _unreadable = true;
            return new SettingsUiState();
        }
    }

    /// <summary>
    /// Replaces the file atomically. Returns false when the write did not land, including the
    /// refusal while the file could not be read.
    /// </summary>
    public async Task<bool> SaveAsync(SettingsUiState state, CancellationToken cancellationToken = default)
    {
        if (_unreadable)
        {
            return false;
        }

        var temporaryPath = _path + TemporaryFileExtension;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await DurableFile.ReplaceAsync(
                _path,
                temporaryPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream, state, _jsonOptions, token),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    // SP-0175: the file is hand-editable and outlives builds, so a document that parses but holds a null
    // dictionary or a null anchor must not reach SettingsWindow, which indexes them without a check. An
    // anchor with no group ID is dropped too - an absent anchor reads as the page top.
    private static SettingsUiState Sanitize(SettingsUiState? state)
    {
        if (state is null)
        {
            return new SettingsUiState();
        }

        return state with
        {
            Groups = state.Groups ?? [],
            Viewports = state.Viewports is null
                ? []
                : state.Viewports
                    .Where(entry => entry.Value is { GroupId: not null })
                    .ToDictionary(entry => entry.Key, entry => entry.Value)
        };
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
