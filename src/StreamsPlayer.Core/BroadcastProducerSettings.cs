using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0217's user settings, in their own small file beside the catalog state (the same two reasons as
/// every sibling document: a catalog save rewrites a ~15 MB document, and this is not a per-channel
/// value). Nothing here is an account fact: the exchange account keeps its own store, and the device
/// token never crosses into this one.
/// </summary>
public sealed record BroadcastProducerSettings
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The permission of the owner's two-step consent: off by default; while it is off no capture API is called.</summary>
    public bool Allowed { get; init; }

    /// <summary>The optional third step: start the chosen sources once the account is online. Off by default.</summary>
    public bool StartWithApplication { get; init; }

    /// <summary>The LAN listening opt-in (constraint 4): off by default; the LAN endpoint exists only while it is on.</summary>
    public bool LanListening { get; init; }

    /// <summary>Section 2.1: the default port is 8768, user-configurable, no fallback - taken means the LAN share does not start.</summary>
    public int LanPort { get; init; } = 8768;

    /// <summary>The microphone the user chose; null is the Windows default capture device.</summary>
    public string? MicrophoneDeviceId { get; init; }

    public int SampleRate { get; init; } = 44100;

    /// <summary>Open question 4: the microphone keeps the contract's mono default, the system sound is stereo; both changeable.</summary>
    public int MicrophoneChannels { get; init; } = 1;

    public int SystemSoundChannels { get; init; } = 2;

    public int BitrateKbps { get; init; } = 128;

    /// <summary>The cap on listeners (requirement 4): 0 means no cap, the default.</summary>
    public int MaxListeners { get; init; }

    /// <summary>
    /// The sources the user chose earlier, by their item G wire names - what a start-with-application
    /// restart starts, and nothing else. A start adds its source, a stop removes it, so the application
    /// never starts a source the user did not leave chosen.
    /// </summary>
    public IReadOnlyList<string> RestartSources { get; init; } = [];
}

/// <summary>
/// Reads and writes `broadcast-settings.json` under the application data folder, on the same
/// boundaries as the settings window's own store: atomic write, an unreadable file is never written
/// over and reads as defaults.
/// </summary>
public sealed class BroadcastProducerSettingsStore
{
    private const string StateFileName = "broadcast-settings.json";
    private const string TemporaryFileExtension = ".tmp";

    private readonly string _path;
    private volatile bool _unreadable;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public BroadcastProducerSettingsStore(string directory) =>
        _path = Path.Combine(directory, StateFileName);

    public string FilePath => _path;

    public async Task<BroadcastProducerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var (state, status, _) = await DurableFile
            .ReadAsync<BroadcastProducerSettings>(_path, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        _unreadable = status == FileReadStatus.Unreadable;
        return Sanitize(state ?? new BroadcastProducerSettings());
    }

    public BroadcastProducerSettings LoadSync()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length == 0)
            {
                _unreadable = false;
                return new BroadcastProducerSettings();
            }

            var state = JsonSerializer.Deserialize<BroadcastProducerSettings>(File.ReadAllText(_path), _jsonOptions);
            _unreadable = false;
            return Sanitize(state ?? new BroadcastProducerSettings());
        }
        catch
        {
            _unreadable = true;
            return new BroadcastProducerSettings();
        }
    }

    public async Task<bool> SaveAsync(BroadcastProducerSettings settings, CancellationToken cancellationToken = default)
    {
        if (_unreadable)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            await DurableFile.ReplaceAsync(_path, _path + TemporaryFileExtension,
                (stream, token) => stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(settings, _jsonOptions), token)
                    .AsTask(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The one place the bounds live: a hand-edited file is foreign input, and a value outside its range reads as its default.</summary>
    private static BroadcastProducerSettings Sanitize(BroadcastProducerSettings settings)
    {
        var restart = settings.RestartSources
            .Where(name => BroadcastSources.TryParseWireName(name, out _))
            .Distinct()
            .Take(BroadcastSources.MaximumSimultaneous)
            .ToArray();
        return settings with
        {
            LanPort = settings.LanPort is >= 1 and <= 65535 ? settings.LanPort : 8768,
            SampleRate = settings.SampleRate is 44100 or 48000 or 32000 or 24000 or 22050 or 16000 ? settings.SampleRate : 44100,
            MicrophoneChannels = settings.MicrophoneChannels is 1 or 2 ? settings.MicrophoneChannels : 1,
            SystemSoundChannels = settings.SystemSoundChannels is 1 or 2 ? settings.SystemSoundChannels : 2,
            BitrateKbps = settings.BitrateKbps is >= 32 and <= 320 ? settings.BitrateKbps : 128,
            MaxListeners = settings.MaxListeners is >= 0 and <= 64 ? settings.MaxListeners : 0,
            MicrophoneDeviceId = string.IsNullOrWhiteSpace(settings.MicrophoneDeviceId) ? null : settings.MicrophoneDeviceId,
            RestartSources = restart
        };
    }
}
