using System.Net.Http;
using System.Net.Http.Headers;
using StreamsPlayer.DrivePremiseHarness;

if (args.Length == 0)
{
    Console.WriteLine("""
        SP-0202 premise experiment (DEVICE-EXCHANGE 8.2): does a file one OAuth client of the project writes to
        Drive's appDataFolder appear in another client's listing of it? Run each command once per client.

        Commands:
          write   --client-id <id> [--label <text>] [--evidence <dir>]
                   Sign in with one Desktop-app client and write the probe record fmsx-broadcast-*.json.
          list    --client-id <id> [--label <text>] [--expect-file <name>] [--evidence <dir>]
                   Sign in with the other client, list the application data folder, read every fmsx-*.json
                   wrapper it finds, and state the verdict for every probe the working tree knows plus the
                   one --expect-file names (use it for a file written by the Android side).
          delete  --client-id <id> --file-id <driveFileId> [--evidence <dir>]
                   Delete one probe file, to leave the account clean.

        --label names the client this run acts as (for example "desktop-A"); --evidence overrides the
        default evidence directory temp/SP-0202. The access token is held in memory only.
        """);
    return 0;
}

var command = args[0];
var options = ParseOptions(args.Skip(1));
var evidenceDirectory = options.GetValueOrDefault("evidence") ?? DrivePremiseEvidence.DefaultDirectory;
var clientId = options.GetValueOrDefault("client-id") ?? throw new ArgumentException("Every command needs --client-id.");
var label = options.GetValueOrDefault("label") ?? "unnamed-client";

using var client = new HttpClient();
client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StreamsPlayer-DrivePremiseHarness", "0.1"));
var drive = new DrivePremiseDrive(client);

try
{
    switch (command)
    {
        case "write":
            await WriteProbeAsync(drive, clientId, label, evidenceDirectory);
            return 0;
        case "list":
            await ListAppDataAsync(drive, clientId, label, options, evidenceDirectory);
            return 0;
        case "delete":
            await DeleteProbeAsync(drive, clientId, options, evidenceDirectory);
            return 0;
        default:
            Console.WriteLine($"Unknown command '{command}'. Run with no arguments for usage.");
            return 2;
    }
}
catch (Exception exception)
{
    var verdict = DrivePremiseEvidence.Verdict(command + " completed", $"error: {exception.Message}");
    Console.WriteLine(verdict);
    DrivePremiseEvidence.Append(evidenceDirectory, new
    {
        Command = command,
        Label = label,
        ClientId = clientId,
        Verdict = verdict,
        ErrorType = exception.GetType().Name,
    });
    return 1;
}

static Dictionary<string, string?> ParseOptions(IEnumerable<string> words)
{
    var options = new Dictionary<string, string?>();
    string? pending = null;
    foreach (var word in words)
    {
        if (pending is null)
        {
            pending = word;
            options[pending] = null;
        }
        else
        {
            options[pending] = word;
            pending = null;
        }
    }
    return options;
}

async Task WriteProbeAsync(DrivePremiseDrive drive, string clientId, string label, string evidenceDirectory)
{
    var accessToken = await DrivePremiseOAuth.SignInAsync(client, clientId, CancellationToken.None);
    var broadcastId = DrivePremiseProbe.NewBroadcastId();
    var fileName = DrivePremiseProbe.FileNameFor(broadcastId);
    var writtenAt = DateTimeOffset.UtcNow;
    var wrapper = DrivePremiseProbe.BuildWrapper(broadcastId, writtenAt);
    var created = await drive.UploadAsync(accessToken, fileName, wrapper, CancellationToken.None);

    DrivePremiseEvidence.SaveProbeMarker(evidenceDirectory, new ProbeMarker(broadcastId, fileName, created.Id, label, writtenAt.ToString("O")));
    var verdict = DrivePremiseEvidence.Verdict("written", $"written (Drive id {created.Id}, modifiedTime {created.ModifiedTime:O})");
    Console.WriteLine();
    Console.WriteLine($"Probe: {fileName}");
    Console.WriteLine(verdict);
    DrivePremiseEvidence.Append(evidenceDirectory, new
    {
        Command = "write",
        Label = label,
        ClientId = clientId,
        ProbeFileName = fileName,
        DriveFileId = created.Id,
        DriveModifiedTime = created.ModifiedTime,
        WrittenAtUtc = writtenAt,
        WrapperBytes = System.Text.Encoding.UTF8.GetByteCount(wrapper),
        Verdict = verdict,
    });
}

async Task ListAppDataAsync(DrivePremiseDrive drive, string clientId, string label, Dictionary<string, string?> options, string evidenceDirectory)
{
    var accessToken = await DrivePremiseOAuth.SignInAsync(client, clientId, CancellationToken.None);
    var files = await drive.ListAppDataAsync(accessToken, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine($"Application data folder of client '{label}': {files.Count} file(s)");
    foreach (var file in files)
    {
        Console.WriteLine($"  {file.Name}  (id {file.Id}, {file.Size ?? 0} bytes, modifiedTime {file.ModifiedTime:O})");
    }

    var expected = new List<string>();
    foreach (var marker in DrivePremiseEvidence.LoadProbeMarkers(evidenceDirectory))
    {
        expected.Add(marker.FileName);
    }
    if (options.TryGetValue("expect-file", out var named) && named is { Length: > 0 })
    {
        expected.Add(named);
    }

    Console.WriteLine();
    var verdicts = new List<string>();
    foreach (var fileName in expected.Distinct())
    {
        var file = files.FirstOrDefault(candidate => candidate.Name == fileName);
        var actual = file is null
            ? "not-listed"
            : "listed";
        var verdict = DrivePremiseEvidence.Verdict("listed", actual);
        verdicts.Add(verdict);
        Console.WriteLine($"{verdict}  [{fileName}]");

        if (file is not null)
        {
            await ReadAndReportWrapperAsync(drive, accessToken, file);
        }
    }
    if (expected.Count == 0)
    {
        Console.WriteLine("No expected probe name was known: run write first, or pass --expect-file for a file the Android side wrote.");
    }

    DrivePremiseEvidence.Append(evidenceDirectory, new
    {
        Command = "list",
        Label = label,
        ClientId = clientId,
        ListedFiles = files.Select(file => new { file.Id, file.Name, file.Size, file.ModifiedTime }).ToArray(),
        ExpectedFiles = expected.Distinct().ToArray(),
        Verdicts = verdicts.ToArray(),
    });
}

async Task ReadAndReportWrapperAsync(DrivePremiseDrive drive, string accessToken, DriveAppDataFile file)
{
    if (!DrivePremiseProbe.IsFmsxName(file.Name))
    {
        return;
    }
    var content = await drive.ReadMediaAsync(accessToken, file.Id, DrivePremiseProbe.MaxWrapperBytes, CancellationToken.None);
    if (content is null)
    {
        Console.WriteLine($"  {file.Name}: over the contract's {DrivePremiseProbe.MaxWrapperBytes} byte record ceiling - skipped, as a conforming reader would.");
        return;
    }
    if (DrivePremiseProbe.TryParseWrapper(content, out var wrapper) && wrapper is not null)
    {
        Console.WriteLine($"  {file.Name}: schemaVersion {wrapper.SchemaVersion}, type {wrapper.Type}, ttlSeconds {wrapper.TtlSeconds}, " +
            $"broadcastId {wrapper.BroadcastId}, deviceId {wrapper.DeviceId}, mode {wrapper.Mode}, title {wrapper.Title}");
    }
    else
    {
        Console.WriteLine($"  {file.Name}: present but not a readable 8.1 wrapper - a conforming reader would skip it.");
    }
}

async Task DeleteProbeAsync(DrivePremiseDrive drive, string clientId, Dictionary<string, string?> options, string evidenceDirectory)
{
    var fileId = options.GetValueOrDefault("file-id") ?? throw new ArgumentException("delete needs --file-id.");
    var accessToken = await DrivePremiseOAuth.SignInAsync(client, clientId, CancellationToken.None);
    await drive.DeleteAsync(accessToken, fileId, CancellationToken.None);
    var verdict = DrivePremiseEvidence.Verdict("deleted", "deleted");
    Console.WriteLine(verdict);
    DrivePremiseEvidence.Append(evidenceDirectory, new { Command = "delete", ClientId = clientId, DriveFileId = fileId, Verdict = verdict });
}
