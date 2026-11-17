using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>SP-0271: one option of a command, as <c>APP-COMMANDS</c> rule 18 declares it.</summary>
public sealed record AppCommandArgument(string Name, string Kind, bool Required, IReadOnlyList<string> Values)
{
    /// <summary>
    /// SP-0279, item H: the value may carry a credential. It is never written to a log line, an event, a <c>data</c> member
    /// or a message, and the verb also takes it from a file the caller names (<c>--&lt;name&gt;-file</c>).
    /// </summary>
    public bool Sensitive { get; init; }
}

/// <summary>
/// SP-0279, <c>APP-COMMANDS</c> item E: one shipped spelling of a command. The manifest writes an alias as plain text or as
/// an object; both read as this record, and <see cref="AppCommandEntry.Aliases"/> keeps the spellings alone.
/// </summary>
public sealed record AppCommandAlias(string Spelling)
{
    /// <summary>Placeholder name to the option that receives the typed value (<c>{ "url": "url" }</c>).</summary>
    public IReadOnlyDictionary<string, string> Maps { get; init; } = new Dictionary<string, string>();

    /// <summary>Options, as typed, that the spelling implies; each is an option the canonical verb has.</summary>
    public IReadOnlyList<string> Implies { get; init; } = [];

    /// <summary>The spelling shipped before the product adopted the command contract (item F).</summary>
    public bool Legacy { get; init; }

    /// <summary><see langword="false"/> when the spelling keeps the start it always made and returns no answer of rule 11.</summary>
    public bool Answers { get; init; } = true;

    /// <summary>The executable that takes the spelling when it is not the command's; empty otherwise.</summary>
    public string Exe { get; init; } = string.Empty;
}

/// <summary>SP-0271: one command of the manifest (<c>APP-COMMANDS</c> rule 18).</summary>
public sealed record AppCommandEntry(
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<AppCommandArgument> Args,
    string Risk,
    string ColdStart,
    string Wait,
    int TimeoutMs,
    bool Idempotent)
{
    /// <summary>The one sentence that says what the command does.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// SP-0275, rule 18 <c>exe</c>: the executable that serves the command when it is not the product's - the console
    /// companion for a command that needs no window of its own. Empty when the product's own executable serves it.
    /// </summary>
    public string Exe { get; init; } = string.Empty;

    /// <summary>SP-0279, item E: the aliases with their qualifiers, in the order the manifest lists them.</summary>
    public IReadOnlyList<AppCommandAlias> AliasDetails { get; init; } = [];

    /// <summary>
    /// SP-0279, item C: <c>always</c> (a running copy does the work) or <c>never</c> (the process that received the command does
    /// it and touches nothing of a first instance).
    /// </summary>
    public string Forward { get; init; } = AppCommandManifest.ForwardAlways;

    public AppCommandArgument? FindArgument(string name) =>
        Args.FirstOrDefault(argument => string.Equals(argument.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// SP-0271: the command manifest of <c>APP-COMMANDS</c> rule 18. <c>docs/cli-commands.json</c> is the one source of
/// truth; it is embedded in this assembly, so <c>commands --json</c> prints the shipped bytes and never a copy written
/// from memory, and both processes parse a command line against the same declarations.
/// </summary>
public sealed class AppCommandManifest
{
    public const string ResourceName = "cli-commands.json";

    public static readonly IReadOnlyList<string> ArgumentKinds = ["string", "path", "int", "bool", "enum", "guid"];

    public static readonly IReadOnlyList<string> RiskClasses = ["safe", "reads-user-data", "writes-user-data", "leaves-machine"];

    public static readonly IReadOnlyList<string> ColdStartModes = ["start", "oneshot", "refuse"];

    public static readonly IReadOnlyList<string> WaitModes = ["done", "accepted"];

    /// <summary>SP-0279, item C: the value of <c>forward</c> that is read when the member is absent.</summary>
    public const string ForwardAlways = "always";

    /// <summary>SP-0279, item C: a verb the process that received it does itself.</summary>
    public const string ForwardNever = "never";

    public static readonly IReadOnlyList<string> ForwardModes = [ForwardAlways, ForwardNever];

    private static readonly Lazy<byte[]> EmbeddedPayload = new(ReadEmbedded);

    private static readonly Lazy<AppCommandManifest> EmbeddedManifest = new(() => Parse(EmbeddedPayload.Value));

    private AppCommandManifest(string product, string exe, string cliVersion, IReadOnlyList<AppCommandEntry> commands)
    {
        Product = product;
        Exe = exe;
        CliVersion = cliVersion;
        Commands = commands;
    }

    public string Product { get; }

    public string Exe { get; }

    public string CliVersion { get; }

    public IReadOnlyList<AppCommandEntry> Commands { get; }

    /// <summary>The manifest this build ships.</summary>
    public static AppCommandManifest Embedded => EmbeddedManifest.Value;

    /// <summary>The shipped manifest's bytes, exactly as <c>docs/cli-commands.json</c> holds them.</summary>
    public static ReadOnlyMemory<byte> EmbeddedBytes => EmbeddedPayload.Value;

    /// <summary>The executable that serves <paramref name="entry"/>: its own <c>exe</c>, else the product's.</summary>
    public string ExeOf(AppCommandEntry entry) => entry.Exe.Length > 0 ? entry.Exe : Exe;

    /// <summary>The command named <paramref name="name"/> (verbs are compared without regard to case, rule 1).</summary>
    public AppCommandEntry? Find(string name) =>
        Commands.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The command that lists <paramref name="alias"/> among its aliases, compared exactly.</summary>
    public AppCommandEntry? FindByAlias(string alias) =>
        Commands.FirstOrDefault(entry => entry.Aliases.Contains(alias, StringComparer.Ordinal));

    /// <summary>
    /// Reads a manifest. Throws <see cref="FormatException"/> for one that breaks rule 18 or rule 10 - a command with
    /// no <c>coldStart</c> is an error of the manifest, not a default.
    /// </summary>
    public static AppCommandManifest Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            ReadOnlySpan<byte> byteOrderMark = [0xEF, 0xBB, 0xBF];
            var bytes = json.StartsWith(byteOrderMark) ? json[3..] : json;
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (RequiredInt(root, "schemaVersion") != 1)
            {
                throw new FormatException("The manifest's schemaVersion is not 1.");
            }

            var commands = new List<AppCommandEntry>();
            foreach (var command in RequiredArray(root, "commands"))
            {
                commands.Add(ParseCommand(command));
            }

            var duplicate = commands.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new FormatException($"The manifest declares '{duplicate.Key}' twice.");
            }

            return new AppCommandManifest(
                RequiredString(root, "product"), RequiredString(root, "exe"), RequiredString(root, "cliVersion"), commands);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The manifest is not valid JSON.", exception);
        }
    }

    private static AppCommandEntry ParseCommand(JsonElement command)
    {
        var name = RequiredString(command, "name");
        if (!AppCommandLine.IsVerb(name) || !string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new FormatException($"'{name}' is not a verb of one or two lower-case words.");
        }

        var args = new List<AppCommandArgument>();
        foreach (var argument in RequiredArray(command, "args"))
        {
            var kind = OneOf(argument, "kind", ArgumentKinds, name);
            var values = argument.TryGetProperty("values", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
                : [];
            if (kind == "enum" && values.Length == 0)
            {
                throw new FormatException($"'{name}' has an enum option without values.");
            }

            args.Add(new AppCommandArgument(
                RequiredString(argument, "name"), kind, RequiredBool(argument, "required"), values)
            {
                Sensitive = OptionalBool(argument, "sensitive")
            });
        }

        var timeout = RequiredInt(command, "timeoutMs");
        if (timeout is < 1 or > AppCommandLimits.MaximumTimeoutMs)
        {
            throw new FormatException($"'{name}' has a timeoutMs outside 1..{AppCommandLimits.MaximumTimeoutMs}.");
        }

        foreach (var member in new[] { "summary", "synopsis" })
        {
            RequiredString(command, member);
        }

        foreach (var member in new[] { "returns", "exitCodes", "examples" })
        {
            RequiredArray(command, member);
        }

        RequiredBool(command, "interactive");
        RequiredString(command, "since");

        var aliases = RequiredArray(command, "aliases").Select(alias => ParseAlias(alias, name)).ToArray();
        var coldStart = OneOf(command, "coldStart", ColdStartModes, name);
        var wait = OneOf(command, "wait", WaitModes, name);
        var forward = ForwardOf(command, name);
        if (forward == ForwardNever && (coldStart != "oneshot" || wait != "done"))
        {
            // Item C paragraph 2: a verb the receiving process does itself starts nothing and waits for nothing.
            throw new FormatException($"'{name}' is forward 'never' and must have coldStart 'oneshot' and wait 'done'.");
        }

        return new AppCommandEntry(
            name,
            aliases.Select(alias => alias.Spelling).ToArray(),
            args,
            OneOf(command, "risk", RiskClasses, name),
            coldStart,
            wait,
            timeout,
            RequiredBool(command, "idempotent"))
        {
            Summary = RequiredString(command, "summary"),
            Exe = command.TryGetProperty("exe", out var exe) && exe.ValueKind == JsonValueKind.String ? exe.GetString()! : string.Empty,
            AliasDetails = aliases,
            Forward = forward
        };
    }

    /// <summary>An alias is text, or an object whose only required member is <c>spelling</c> (item E); a reader skips what it does not know.</summary>
    private static AppCommandAlias ParseAlias(JsonElement alias, string command)
    {
        if (alias.ValueKind == JsonValueKind.String)
        {
            return new AppCommandAlias(alias.GetString()!);
        }

        if (alias.ValueKind != JsonValueKind.Object ||
            !alias.TryGetProperty("spelling", out var spelling) || spelling.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"'{command}' has an alias that is neither text nor an object with a spelling.");
        }

        var maps = new Dictionary<string, string>(StringComparer.Ordinal);
        if (alias.TryGetProperty("maps", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in map.EnumerateObject())
            {
                if (pair.Value.ValueKind == JsonValueKind.String)
                {
                    maps[pair.Name] = pair.Value.GetString()!;
                }
            }
        }

        var implies = alias.TryGetProperty("implies", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(option => option.ValueKind == JsonValueKind.String).Select(option => option.GetString()!).ToArray()
            : [];
        return new AppCommandAlias(spelling.GetString()!)
        {
            Maps = maps,
            Implies = implies,
            Legacy = OptionalBool(alias, "legacy"),
            Answers = !alias.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.False,
            Exe = alias.TryGetProperty("exe", out var aliasExe) && aliasExe.ValueKind == JsonValueKind.String ? aliasExe.GetString()! : string.Empty
        };
    }

    private static string ForwardOf(JsonElement command, string name)
    {
        if (!command.TryGetProperty("forward", out var value))
        {
            return ForwardAlways;
        }

        var forward = value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
        return ForwardModes.Contains(forward, StringComparer.Ordinal)
            ? forward
            : throw new FormatException($"'{name}' has forward '{forward}', not one of {string.Join(", ", ForwardModes)}.");
    }

    private static bool OptionalBool(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.True;

    private static string OneOf(JsonElement element, string member, IReadOnlyList<string> allowed, string command)
    {
        var value = RequiredString(element, member);
        return allowed.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new FormatException($"'{command}' has {member} '{value}', not one of {string.Join(", ", allowed)}.");
    }

    private static string RequiredString(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"The manifest is missing the string '{member}'.");

    private static int RequiredInt(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new FormatException($"The manifest is missing the integer '{member}'.");

    private static bool RequiredBool(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new FormatException($"The manifest is missing the boolean '{member}'.");

    private static JsonElement.ArrayEnumerator RequiredArray(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw new FormatException($"The manifest is missing the array '{member}'.");

    private static byte[] ReadEmbedded()
    {
        using var resource = typeof(AppCommandManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing from this build.");
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    }
}
