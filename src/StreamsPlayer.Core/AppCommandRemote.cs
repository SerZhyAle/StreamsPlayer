using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamsPlayer.Core;

/// <summary>
/// STRWIN-0303: the manifest member <c>remote</c> of <c>REMOTE-CONTROL</c> rule 7 - <c>confirm</c> (<c>grant</c>: the
/// target's grants decide; <c>device</c>: the person confirms every request on this device) and the optional
/// <c>since</c>, the <c>cliVersion</c> that made the command remote.
/// </summary>
public sealed record AppCommandRemote(string Confirm, string? Since)
{
    public const string ConfirmGrant = "grant";
    public const string ConfirmDevice = "device";

    public bool ConfirmsOnDevice => string.Equals(Confirm, ConfirmDevice, StringComparison.Ordinal);

    /// <summary>The member of <paramref name="command"/>, or null when it has none. A malformed member breaks the manifest.</summary>
    internal static AppCommandRemote? Read(JsonElement command, string name)
    {
        if (!command.TryGetProperty("remote", out var remote))
        {
            return null;
        }

        if (remote.ValueKind != JsonValueKind.Object
            || !remote.TryGetProperty("confirm", out var confirm) || confirm.ValueKind != JsonValueKind.String
            || confirm.GetString() is not (ConfirmGrant or ConfirmDevice))
        {
            throw new FormatException($"'{name}' has a remote member without confirm 'grant' or 'device'.");
        }

        var since = remote.TryGetProperty("since", out var sinceElement) && sinceElement.ValueKind == JsonValueKind.String
            ? sinceElement.GetString()
            : null;
        return new AppCommandRemote(confirm.GetString()!, since);
    }
}

/// <summary>
/// STRWIN-0303: what another device may pass and see. Rule 6 forbids a path the controller names, so an option of kind
/// <c>path</c> is never a remote argument; <c>json</c> shapes a console's output and means nothing in a sealed answer.
/// </summary>
public static class RemoteCommandArguments
{
    public const string PathKind = "path";
    public const string JsonOption = "json";

    public static bool IsRemote(AppCommandArgument argument) =>
        !string.Equals(argument.Kind, PathKind, StringComparison.Ordinal)
        && !string.Equals(argument.Name, JsonOption, StringComparison.Ordinal);

    /// <summary>
    /// The command-line words of <paramref name="args"/> (one member per manifest option, named and typed as it declares),
    /// which <see cref="AppCommandLine.Parse"/> then checks like any other caller's. Null with <paramref name="refusal"/>
    /// set for a member the entry does not take remotely or a value of the wrong JSON type: <c>bad-argument</c>, and
    /// nothing runs (section 3, "an unknown member of a request is never ignored").
    /// </summary>
    public static IReadOnlyList<string>? ToWords(
        AppCommandEntry entry, JsonElement args, string requestId, out AppCommandResponse? refusal)
    {
        refusal = null;
        var words = new List<string>();
        if (args.ValueKind != JsonValueKind.Object)
        {
            return words;
        }

        foreach (var member in args.EnumerateObject())
        {
            var argument = entry.Args.FirstOrDefault(candidate => string.Equals(candidate.Name, member.Name, StringComparison.Ordinal));
            var value = argument is not null && IsRemote(argument) ? ValueOf(argument, member.Value) : null;
            if (value is null)
            {
                refusal = AppCommandResponse.Refused(requestId, AppCommandReason.BadArgument, AppCommandExit.Usage,
                    AppCommandMessages.UnknownOption(member.Name, entry.Name));
                return null;
            }

            words.Add("--" + argument!.Name);
            words.Add(value);
        }

        return words;
    }

    /// <summary>
    /// The answer of <c>commands</c> asked from another device: the shipped manifest with only the entries that carry
    /// <c>remote</c>, each without the options it does not take remotely (rule 7).
    /// </summary>
    public static JsonObject RemoteView(ReadOnlySpan<byte> manifestJson)
    {
        ReadOnlySpan<byte> byteOrderMark = [0xEF, 0xBB, 0xBF];
        var root = JsonNode.Parse(manifestJson.StartsWith(byteOrderMark) ? manifestJson[3..] : manifestJson)!.AsObject();
        var kept = new JsonArray();
        foreach (var command in root["commands"]!.AsArray().OfType<JsonObject>())
        {
            if (command["remote"] is not JsonObject)
            {
                continue;
            }

            var copy = command.DeepClone().AsObject();
            if (copy["args"] is JsonArray argsArray)
            {
                var remoteArgs = new JsonArray();
                foreach (var argument in argsArray.OfType<JsonObject>())
                {
                    var kind = argument["kind"]?.GetValue<string>();
                    var name = argument["name"]?.GetValue<string>();
                    if (!string.Equals(kind, PathKind, StringComparison.Ordinal) && !string.Equals(name, JsonOption, StringComparison.Ordinal))
                    {
                        remoteArgs.Add(argument.DeepClone());
                    }
                }

                copy["args"] = remoteArgs;
            }

            kept.Add(copy);
        }

        root["commands"] = kept;
        return root;
    }

    private static string? ValueOf(AppCommandArgument argument, JsonElement value) => argument.Kind switch
    {
        "bool" => value.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        },
        "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null,
        _ => value.ValueKind == JsonValueKind.String ? value.GetString() : null
    };
}
