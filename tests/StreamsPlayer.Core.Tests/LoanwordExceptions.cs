namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0034: the recorded, per-language list of keys whose correct translation is the English word.
/// <para>
/// Read from <see cref="FileName"/> rather than hardcoded, so adding a language does not mean editing a
/// test, and so the list reads as reviewable data with a reason beside each entry.
/// </para>
/// </summary>
internal sealed class LoanwordExceptions
{
    internal const string FileName = "localization-loanwords.txt";

    private readonly HashSet<(string Code, string Key)> _entries;

    private LoanwordExceptions(HashSet<(string, string)> entries) => _entries = entries;

    internal IReadOnlyCollection<(string Code, string Key)> Entries => _entries;

    internal bool IsAllowed(string code, string key) =>
        _entries.Contains((code.ToLowerInvariant(), key));

    internal static LoanwordExceptions Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        var entries = new HashSet<(string, string)>();
        if (!File.Exists(path))
        {
            return new LoanwordExceptions(entries);
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            // "code:Key # why". Everything from the hash is the reason and is required by review, not by
            // the parser.
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException(
                    $"{FileName}: expected \"code:Key # reason\", found \"{raw}\".");
            }

            entries.Add((
                line[..separator].Trim().ToLowerInvariant(),
                line[(separator + 1)..].Trim()));
        }

        return new LoanwordExceptions(entries);
    }
}
