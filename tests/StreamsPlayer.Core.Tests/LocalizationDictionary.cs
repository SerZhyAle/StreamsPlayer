using System.Text;
using System.Xml.Linq;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// One parsed <c>Localization.&lt;code&gt;.xaml</c>, read as XML data rather than as a WPF resource
/// dictionary - the test project depends on Core only and cannot load BAML (SP-0034).
/// </summary>
internal sealed record LocalizationDictionary(
    string Code,
    string Path,
    bool HasByteOrderMark,
    IReadOnlyList<string> KeysInFileOrder,
    IReadOnlyDictionary<string, string> Values)
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Where the linked dictionaries land beside the test assembly.</summary>
    internal static string Directory => System.IO.Path.Combine(AppContext.BaseDirectory, "localization");

    internal static IReadOnlyList<LocalizationDictionary> LoadAll()
    {
        var files = System.IO.Directory.GetFiles(Directory, "Localization.*.xaml").OrderBy(path => path).ToArray();
        Assert.NotEmpty(files);
        return [.. files.Select(Load)];
    }

    internal static LocalizationDictionary Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(
            bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var code = name["Localization.".Length..];
        return Parse(code, path, hasBom, text);
    }

    internal static LocalizationDictionary Parse(string code, string path, bool hasBom, string text)
    {
        var root = XDocument.Parse(text).Root
            ?? throw new InvalidOperationException($"{path} has no root element.");

        var keys = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.Elements())
        {
            var key = element.Attribute(Xaml + "Key")?.Value;
            if (key is null)
            {
                continue;
            }

            keys.Add(key);
            // A duplicate key is reported by the caller comparing counts; keep the first so the rest of
            // the comparison still has something to work with.
            values.TryAdd(key, element.Value);
        }

        return new LocalizationDictionary(code, path, hasBom, keys, values);
    }

    /// <summary>
    /// The multiset of composite-format argument indices in a value, ordered, so a dropped, duplicated
    /// or renumbered <c>{0}</c> is a difference.
    /// </summary>
    /// <remarks>
    /// SP-0057: the application's own parser, not a second copy of one. The parity gate and the arity gate
    /// have to mean the same thing by "placeholder", and the arity gate has to mean the same thing the
    /// runtime does - three definitions were two too many. The only behavioural difference from the regex
    /// this replaced is that <c>{{</c> is now correctly read as an escape; no shipped value uses one.
    /// </remarks>
    internal static IReadOnlyList<int> PlaceholderIndices(string value) =>
        LocalizedFormat.PlaceholderIndices(value);
}
