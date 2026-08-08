using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// One way the application reaches a localized string, and how many format arguments that way supplies.
/// </summary>
/// <param name="Method">The method name, qualified when the receiver is what disambiguates it.</param>
/// <param name="KeyArgument">Which argument holds the resource key.</param>
/// <param name="FormatArguments">
/// How many format arguments the key receives, or <c>null</c> when it is simply the rest of the call.
/// A fixed number is for a wrapper that takes the key as a parameter and supplies the arguments itself,
/// so the count is not visible at the caller.
/// </param>
internal sealed record LocalizedEntryPoint(string Method, int KeyArgument, int? FormatArguments);

/// <summary>What one gate pass found, including how much it looked at.</summary>
internal sealed record GateReport(IReadOnlyList<string> Problems, int CallSites, IReadOnlyCollection<string> Keys);

/// <summary>
/// SP-0057: compares the shipped strings against the code that formats them.
/// </summary>
/// <remarks>
/// <c>string.Format</c> is asymmetric - it ignores surplus arguments in silence and throws on a shortfall -
/// and every caller here is an <c>async void</c> event handler that does not filter
/// <see cref="FormatException"/>. So a string that gains a placeholder without its call site gaining an
/// argument is not a wrong status line, it is the process ending. Nothing compared the two until this.
/// </remarks>
internal static class LocalizedCallSiteGate
{
    /// <summary>
    /// Paths that render a localized string, where the key must exist and the argument count must match.
    /// </summary>
    internal static readonly IReadOnlyList<LocalizedEntryPoint> FormattingEntryPoints =
    [
        // The one renderer. Everything below eventually calls it.
        new("LocalizationService.Format", 0, null),
        // Its no-argument sibling: a key with placeholders reached this way renders "{0}" to the user.
        new("LocalizationService.Get", 0, 0),
        new("SetStatus", 0, null),
        new("SetNowPlaying", 0, null),
        new("SetWaitText", 0, null),
        // Binds the key as a resource reference, so it can supply nothing.
        new("SetWaitTextResource", 0, 0),
        // The two progress wrappers take their keys as parameters and format them in MainWindow.Progress.cs,
        // so the counts below mirror that file rather than the caller. ShowDownloadProgress passes percent,
        // received and total to its percentKey; received alone to its unknownKey; nothing to completionKey.
        new("ShowDownloadProgress", 1, 3),
        new("ShowDownloadProgress", 2, 1),
        new("ShowDownloadProgress", 3, 0),
        // ShowCountProgress passes processed and total.
        new("ShowCountProgress", 2, 2)
    ];

    /// <summary>
    /// Paths that bind a resource by key and can never supply an argument.
    /// </summary>
    /// <remarks>
    /// The key is not required to be a localized string here - the same call binds styles and brushes - so
    /// only one thing is checked: that it is not a *localized* string carrying a placeholder, which would
    /// print its own braces to the user.
    /// </remarks>
    internal static readonly IReadOnlyList<LocalizedEntryPoint> ResourceReferenceEntryPoints =
    [
        new("SetResourceReference", 1, 0)
    ];

    private static readonly Regex MarkupReference = new(
        @"\{\s*(?:Dynamic|Static)Resource\s+(?<key>[^}\s]+)\s*\}",
        RegexOptions.Compiled);

    internal static GateReport Inspect(
        IEnumerable<AppSourceFile> sources,
        IReadOnlyDictionary<string, string> english)
    {
        var problems = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var callSites = 0;

        foreach (var source in sources)
        {
            foreach (var entryPoint in FormattingEntryPoints)
            {
                foreach (var call in source.Invocations(entryPoint.Method))
                {
                    if (call.Arguments.Count <= entryPoint.KeyArgument)
                    {
                        // The table says a key sits here and the call has no such argument. Reported
                        // rather than skipped: it means the table has gone stale, and a stale table is a
                        // hole in the gate.
                        problems.Add(
                            $"{Where(source, call)}: {entryPoint.Method} has {call.Arguments.Count} arguments, " +
                            $"so there is no argument {entryPoint.KeyArgument} to hold a resource key.");
                        continue;
                    }

                    var literals = source.LiteralsIn(call.Arguments[entryPoint.KeyArgument]);
                    if (literals.Count == 0)
                    {
                        continue; // A variable key. Nothing readable is there.
                    }

                    callSites++;
                    var supplied = entryPoint.FormatArguments
                        ?? call.Arguments.Count - entryPoint.KeyArgument - 1;

                    foreach (var key in literals)
                    {
                        keys.Add(key);
                        if (!english.TryGetValue(key, out var value))
                        {
                            problems.Add(
                                $"{Where(source, call)}: {entryPoint.Method} names \"{key}\", which is not a key " +
                                "in Localization.en.xaml. At runtime the lookup returns the key name and the " +
                                "user reads it verbatim.");
                            continue;
                        }

                        var required = LocalizedFormat.RequiredArgumentCount(value);
                        if (supplied == required)
                        {
                            continue;
                        }

                        problems.Add(
                            $"{Where(source, call)}: \"{key}\" needs {required} format argument(s) but " +
                            $"{entryPoint.Method} supplies {supplied}. The string is \"{Trim(value)}\". " +
                            (supplied < required
                                ? "At runtime this is a FormatException inside an async void handler."
                                : "The surplus is ignored at runtime, so nothing else would ever report it."));
                    }
                }
            }

            foreach (var entryPoint in ResourceReferenceEntryPoints)
            {
                foreach (var call in source.Invocations(entryPoint.Method))
                {
                    if (call.Arguments.Count <= entryPoint.KeyArgument)
                    {
                        continue; // A different overload; this path binds no localized string.
                    }

                    foreach (var key in source.LiteralsIn(call.Arguments[entryPoint.KeyArgument]))
                    {
                        problems.AddRange(PlaceholderKeyProblem(Where(source, call), entryPoint.Method, key, english));
                    }
                }
            }
        }

        return new GateReport(problems, callSites, keys);
    }

    internal static IReadOnlyList<string> InspectMarkup(
        IEnumerable<AppSourceFile> markup,
        IReadOnlyDictionary<string, string> english)
    {
        var problems = new List<string>();
        foreach (var file in markup)
        {
            foreach (Match match in MarkupReference.Matches(file.Text))
            {
                var where = $"{file.Name}:{file.LineAt(match.Index)}";
                problems.AddRange(PlaceholderKeyProblem(where, "a resource binding", match.Groups["key"].Value, english));
            }
        }

        return problems;
    }

    private static IEnumerable<string> PlaceholderKeyProblem(
        string where,
        string path,
        string key,
        IReadOnlyDictionary<string, string> english)
    {
        // Not being a localized string at all is fine here: these paths bind styles and brushes too.
        if (!english.TryGetValue(key, out var value))
        {
            yield break;
        }

        var required = LocalizedFormat.RequiredArgumentCount(value);
        if (required == 0)
        {
            yield break;
        }

        yield return
            $"{where}: \"{key}\" is bound through {path}, which supplies no arguments, but the string " +
            $"needs {required}. The user would read the braces: \"{Trim(value)}\".";
    }

    private static string Where(AppSourceFile source, Invocation call) =>
        $"{source.Name}:{source.LineAt(call.Offset)}";

    private static string Trim(string value) => value.Length <= 60 ? value : value[..57] + "...";
}
