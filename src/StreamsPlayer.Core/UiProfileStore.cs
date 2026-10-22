using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0231: reads and writes <c>ui-profile.json</c> beside the catalog state - the interface size and the
/// input profile the user chose (<c>UI-SIZING</c> section 2).
/// </summary>
/// <remarks>
/// Same boundaries as <see cref="SettingsUiStateStore"/>, for the same two reasons that keep a per-surface value
/// out of <see cref="CatalogState"/>: a save of that document rewrites every channel, and losing this file costs
/// only what it remembered. The names are stored as text, not as numbers, and an unknown or missing name reads as
/// the ordinary value for that field alone, so a file written by a newer build costs one field, never the pair.
/// A file that could not be read is never written over.
/// </remarks>
public sealed class UiProfileStore
{
    private const string StateFileName = "ui-profile.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _path;
    private volatile bool _unreadable;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public UiProfileStore(string directory) =>
        _path = Path.Combine(directory, StateFileName);

    /// <summary>The file this store reads and writes. Not named <c>Path</c>: that shadows System.IO.Path.</summary>
    public string FilePath => _path;

    /// <summary>
    /// The synchronous read the application's start-up uses: the profile has to be decided before the first
    /// window is built, and a constructor cannot await. The file is a few dozen bytes.
    /// </summary>
    public UiProfile LoadSync()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length == 0)
            {
                _unreadable = false;
                return UiProfile.Default;
            }

            var document = JsonSerializer.Deserialize<UiProfileDocument>(File.ReadAllText(_path), _jsonOptions);
            _unreadable = false;
            return FromDocument(document);
        }
        catch
        {
            _unreadable = true;
            return UiProfile.Default;
        }
    }

    /// <summary>
    /// Replaces the file atomically. Returns false when the write did not land, including the refusal while
    /// the file could not be read.
    /// </summary>
    public async Task<bool> SaveAsync(UiProfile profile, CancellationToken cancellationToken = default)
    {
        if (_unreadable)
        {
            return false;
        }

        var temporaryPath = _path + TemporaryFileExtension;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var document = new UiProfileDocument(ToName(profile.Input), ToName(profile.Size));
            await DurableFile.ReplaceAsync(
                _path,
                temporaryPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream, document, _jsonOptions, token),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    internal static UiProfile FromDocument(UiProfileDocument? document) =>
        new(
            ParseName(document?.Input, UiProfile.Default.Input),
            ParseName(document?.Size, UiProfile.Default.Size));

    // Enum.TryParse also accepts "1" and "-3" as numbers, which would let a stray digit pick a member by its
    // position in a list a later build may reorder. Only a name is a name.
    private static T ParseName<T>(string? text, T fallback) where T : struct, Enum =>
        text is { Length: > 0 } && char.IsLetter(text[0])
        && Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value)
            ? value
            : fallback;

    private static string ToName<T>(T value) where T : struct, Enum => value.ToString();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stranded temp file is a few bytes and the next save overwrites it; failing to delete it must
            // not turn a best-effort write into a reported error.
        }
    }
}

/// <summary>The on-disk shape of <c>ui-profile.json</c>: names, so an unknown one is a field to default, not an error.</summary>
internal sealed record UiProfileDocument(string? Input, string? Size);
