using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropFlow.Core.Catalog;

public sealed record CatalogCategory(
    string Id,
    string En,
    string Ar,
    int Phase,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyDictionary<string, int>? LateOutputs,
    string Engine,
    IReadOnlyList<string> Platforms);

public sealed record CatalogTool(
    string Id,
    string? Cat,
    string? Wheel,
    string En,
    string Ar,
    string Engine,
    IReadOnlyList<string> Platforms,
    int Phase,
    bool Multi = false,
    string? Tg = null,
    int? MinFiles = null,
    int? MaxFiles = null,
    IReadOnlyList<string>? AlsoIn = null)
{
    public string Name(string language) => language == "ar" ? Ar : En;

    /// <summary>
    /// True when the tool belongs on the wheel of input category <paramref name="categoryId"/>: its own <see cref="Cat"/>, or one listed in
    /// <see cref="AlsoIn"/> ("Trim silence" is an audio tool that also works on the sound of a video).
    /// </summary>
    public bool AppliesTo(string? categoryId) =>
        categoryId is not null && (Cat == categoryId || (AlsoIn?.Contains(categoryId) ?? false));

    /// <summary>
    /// How many files the tool needs before the wheel offers it. A <see cref="Multi"/> tool (merge, join, collage) defaults to 2;
    /// "Compress to ZIP" sets <c>minFiles: 1</c> because one file or folder is a fine thing to zip.
    /// </summary>
    public int MinimumFiles => Multi ? MinFiles ?? 2 : 1;

    /// <summary>The most files the tool accepts, or null for no limit ("Compare PDFs" takes exactly two).</summary>
    public bool Accepts(int fileCount) => fileCount >= MinimumFiles && (MaxFiles is not { } max || fileCount <= max);
}

/// <summary>The parsed catalog/tools.json. Immutable after load.</summary>
public sealed class ToolCatalog
{
    // Which category wins when an extension appears in several (e.g. "gif" is both an image and a video input).
    private static readonly string[] Priority =
        ["image", "audio", "video", "pdf", "document", "slides", "spreadsheet", "epub", "subtitle", "archive"];

    private readonly Dictionary<string, CatalogCategory> _categories;
    private readonly Dictionary<string, string> _extensionToCategory = new(StringComparer.OrdinalIgnoreCase);

    public ToolCatalog(IEnumerable<CatalogCategory> categories, IEnumerable<CatalogTool> tools, IEnumerable<CatalogTool> actions)
    {
        Categories = categories.ToList();
        Tools = tools.ToList();
        Actions = actions.ToList();
        _categories = Categories.ToDictionary(c => c.Id);

        foreach (var id in Priority)
        {
            if (!_categories.TryGetValue(id, out var cat)) continue;
            foreach (var ext in cat.Inputs) _extensionToCategory.TryAdd(ext, id);
        }
    }

    public IReadOnlyList<CatalogCategory> Categories { get; }
    public IReadOnlyList<CatalogTool> Tools { get; }
    public IReadOnlyList<CatalogTool> Actions { get; }

    public CatalogCategory? Category(string id) => _categories.GetValueOrDefault(id);

    public CatalogTool? Tool(string id) => Tools.FirstOrDefault(t => t.Id == id) ?? Actions.FirstOrDefault(t => t.Id == id);

    // Alternative spellings of the same format. Converters and the catalog only ever see the canonical form.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = "jpg", ["jpe"] = "jpg", ["jfif"] = "jpg",
        ["tif"] = "tiff",
        ["heif"] = "heic",
        ["aif"] = "aiff", ["oga"] = "ogg",
        ["htm"] = "html", ["markdown"] = "md", ["text"] = "txt",
        ["tar.gz"] = "tgz",
    };

    /// <summary>Canonical lower-case extension without the dot ("JPEG" and ".jpe" become "jpg").</summary>
    public static string Normalize(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return Aliases.GetValueOrDefault(ext, ext);
    }

    /// <summary>Category for a file extension without the dot, or null when DropFlow has no conversions for it.</summary>
    public string? CategoryForExtension(string extension) =>
        _extensionToCategory.GetValueOrDefault(Normalize(extension));

    /// <summary>Canonical extension of a path (also recognises ".tar.gz" as "tgz").</summary>
    public static string ExtensionOf(string path)
    {
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return "tgz";
        return Normalize(Path.GetExtension(path));
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Raw(
        List<CatalogCategory> Categories,
        List<CatalogTool> Tools,
        List<CatalogTool> Actions);

    public static ToolCatalog Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<Raw>(json, Json) ?? throw new InvalidDataException("Empty catalog");
        return new ToolCatalog(raw.Categories, raw.Tools, raw.Actions);
    }

    /// <summary>Loads the catalog embedded in this assembly.</summary>
    public static ToolCatalog LoadEmbedded()
    {
        using var stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream("DropFlow.Core.tools.json")
            ?? throw new InvalidOperationException("Embedded catalog is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
