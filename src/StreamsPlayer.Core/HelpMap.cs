using System.Text.Json;

namespace StreamsPlayer.Core;

/// <summary>The form of a surface's help (CONTEXT-HELP rule 4).</summary>
public enum HelpForm
{
    Layer,
    Page,
    Both,
    Inherit,
    None
}

/// <summary>One settings page: a section of the Settings help page, reached by its anchor.</summary>
public sealed record HelpSettingsPage(string Id, string Name, string? NameKey, string Anchor);

/// <summary>One row of the help map: a window and what its help is.</summary>
/// <param name="Help">The permanent help id; the page file is <c>&lt;locale&gt;/&lt;Help&gt;.html</c>. Null for inherit and none.</param>
/// <param name="Anchor">Where in the page the help opens. On an inherit row it is the anchor in the parent's page.</param>
/// <param name="ClassName">The <c>Window</c> subclass the row describes; the gate compares these with the sources.</param>
public sealed record HelpSurface(
    string Id,
    string Name,
    string? NameKey,
    HelpForm Form,
    string? Help,
    string? Anchor,
    string? Parent,
    string? Reason,
    string? ClassName,
    string? Slice,
    IReadOnlyList<HelpSettingsPage> Pages);

/// <summary>
/// SP-0230: the help map of CONTEXT-HELP rule 17, read from the one file the resolver and the gate share.
/// </summary>
/// <remarks>
/// The file lives in the repository's <c>help/</c> folder and is embedded here, so a packaged build and a test
/// read the same bytes. The reader ignores members it does not know (the contract's compatibility clause) and
/// refuses a form it cannot place rather than guessing one: a row with no form is a defect the gate names.
/// </remarks>
public sealed class HelpMap
{
    private const string ResourceName = "help-map.json";

    private readonly Dictionary<string, HelpSurface> _byId;
    private readonly IReadOnlyList<string> _readProblems;

    private HelpMap(int schemaVersion, string product, string index, string sourceLocale, IReadOnlyList<HelpSurface> surfaces, IReadOnlyList<string> readProblems)
    {
        SchemaVersion = schemaVersion;
        Product = product;
        Index = index;
        SourceLocale = sourceLocale;
        Surfaces = surfaces;
        _readProblems = readProblems;
        _byId = new Dictionary<string, HelpSurface>(StringComparer.Ordinal);
        foreach (var surface in surfaces)
        {
            // A duplicate id keeps its first row; Problems() reports the second, so a bad map still loads
            // far enough for the gate to say what is wrong with it.
            _byId.TryAdd(surface.Id, surface);
        }
    }

    public int SchemaVersion { get; }

    public string Product { get; }

    /// <summary>The help id of the index page every locale carries.</summary>
    public string Index { get; }

    /// <summary>The locale a missing page falls back to (CONTEXT-HELP rule 13).</summary>
    public string SourceLocale { get; }

    public IReadOnlyList<HelpSurface> Surfaces { get; }

    /// <summary>The map embedded in this assembly.</summary>
    public static HelpMap Load()
    {
        using var resource = typeof(HelpMap).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The help map is not embedded in {typeof(HelpMap).Assembly.GetName().Name}.");
        using var reader = new StreamReader(resource);
        return Parse(reader.ReadToEnd());
    }

    public static HelpMap Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        var surfaces = new List<HelpSurface>();
        var readProblems = new List<string>();
        if (root.TryGetProperty("surfaces", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            surfaces.AddRange(rows.EnumerateArray().Select(row => ReadSurface(row, readProblems)));
        }

        return new HelpMap(
            root.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var number) ? number : 0,
            Text(root, "product") ?? string.Empty,
            Text(root, "index") ?? "index",
            Text(root, "sourceLocale") ?? "en",
            surfaces,
            readProblems);
    }

    public HelpSurface? Find(string id) => _byId.GetValueOrDefault(id);

    public HelpSurface? FindByClass(string className) =>
        Surfaces.FirstOrDefault(surface => string.Equals(surface.ClassName, className, StringComparison.Ordinal));

    /// <summary>
    /// What is wrong with the map itself, as sentences. Empty when it is sound. The gate asserts it empty; the
    /// resolver does not call it, because a window must never lose F1 over a defect only a maintainer can fix.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>(_readProblems);
        if (SchemaVersion != 1)
        {
            problems.Add($"schemaVersion is {SchemaVersion}, this reader understands 1");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in Surfaces)
        {
            if (!seen.Add(surface.Id))
            {
                problems.Add($"surface '{surface.Id}' appears twice");
            }

            switch (surface.Form)
            {
                case HelpForm.Inherit:
                    if (string.IsNullOrWhiteSpace(surface.Reason))
                    {
                        problems.Add($"surface '{surface.Id}' is inherit with no reason");
                    }

                    if (string.IsNullOrEmpty(surface.Parent) || Find(surface.Parent) is null)
                    {
                        problems.Add($"surface '{surface.Id}' inherits from '{surface.Parent}', which is no surface");
                    }

                    break;
                case HelpForm.None:
                    if (string.IsNullOrWhiteSpace(surface.Reason))
                    {
                        problems.Add($"surface '{surface.Id}' is none with no reason");
                    }

                    break;
                default:
                    if (string.IsNullOrWhiteSpace(surface.Help))
                    {
                        problems.Add($"surface '{surface.Id}' has the form {surface.Form} and no help id");
                    }

                    break;
            }

            if (surface.Form == HelpForm.Inherit && HelpResolver.Resolve(this, surface.Id) is null)
            {
                problems.Add($"surface '{surface.Id}' inherits a chain that ends in no help");
            }
        }

        return problems;
    }

    private static HelpSurface ReadSurface(JsonElement row, List<string> readProblems)
    {
        var rawForm = Text(row, "form");
        var form = rawForm switch
        {
            "layer" => HelpForm.Layer,
            "page" => HelpForm.Page,
            "both" => HelpForm.Both,
            "inherit" => HelpForm.Inherit,
            "none" => HelpForm.None,
            // An unreadable form degrades to none so one bad row costs that window its help, not every window,
            // and is remembered here because the surface row alone cannot say it was not meant.
            _ => HelpForm.None
        };
        if (rawForm is not ("layer" or "page" or "both" or "inherit" or "none"))
        {
            readProblems.Add($"surface '{Text(row, "id")}' has the form '{rawForm}', which the reader does not know");
        }

        var pages = new List<HelpSettingsPage>();
        if (row.TryGetProperty("pages", out var rawPages) && rawPages.ValueKind == JsonValueKind.Array)
        {
            pages.AddRange(rawPages.EnumerateArray().Select(page => new HelpSettingsPage(
                Text(page, "id") ?? string.Empty,
                Text(page, "name") ?? string.Empty,
                Text(page, "nameKey"),
                Text(page, "anchor") ?? string.Empty)));
        }

        return new HelpSurface(
            Text(row, "id") ?? string.Empty,
            Text(row, "name") ?? string.Empty,
            Text(row, "nameKey"),
            form,
            Text(row, "help"),
            Text(row, "anchor"),
            Text(row, "parent"),
            Text(row, "reason"),
            Text(row, "class"),
            Text(row, "slice"),
            pages);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
