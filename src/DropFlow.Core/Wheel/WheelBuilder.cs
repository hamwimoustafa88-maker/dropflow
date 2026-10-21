using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Settings;

namespace DropFlow.Core.Wheel;

public enum WheelKind { Convert, Tools, Actions }

public enum WedgeState { Ready, NeedsEngine }

/// <summary>
/// One slice of a wheel. <paramref name="Wheel"/> is the family the operation belongs to (it decides how the item runs); a pinned
/// wedge keeps its family even when it sits on another wheel. <paramref name="Pinned"/> marks items the user added with "+ Add".
/// </summary>
public sealed record Wedge(
    string Id,
    WheelKind Wheel,
    string Label,
    string Operation,
    WedgeState State = WedgeState.Ready,
    EngineId Engine = EngineId.None,
    bool NeedsOptions = false,
    bool Multi = false,
    bool Pinned = false)
{
    /// <summary>Id of the "+ Add" slot, the last wedge of a wheel when more items could be added to it.</summary>
    public const string AddId = "wheel.add";

    /// <summary>The "+ Add" slot: choosing it opens the list of everything that can be added, it runs nothing itself.</summary>
    public bool IsAdd => Id == AddId;
}

public sealed record WheelSet(IReadOnlyList<Wedge> Convert, IReadOnlyList<Wedge> Tools, IReadOnlyList<Wedge> Actions)
{
    public static readonly WheelSet Empty = new([], [], []);

    /// <summary>Input category the pins are kept under ("image", "pdf"… or <see cref="WheelBuilder.OtherCategory"/>).</summary>
    public string Category { get; init; } = WheelBuilder.OtherCategory;

    /// <summary>What "+ Add" lists for each wheel. Empty unless built with pins.</summary>
    public IReadOnlyDictionary<WheelKind, IReadOnlyList<Wedge>> Addable { get; init; } = new Dictionary<WheelKind, IReadOnlyList<Wedge>>();

    public IReadOnlyList<Wedge> Of(WheelKind kind) => kind switch
    {
        WheelKind.Convert => Convert,
        WheelKind.Tools => Tools,
        _ => Actions,
    };

    /// <summary>Items that could be added to a wheel, grouped Convert → Tools → Actions in catalog order.</summary>
    public IReadOnlyList<Wedge> AddableFor(WheelKind kind) => Addable.GetValueOrDefault(kind) ?? [];
}

/// <summary>
/// Decides what each wheel shows for a selection of files. Only operations that have a registered converter appear, so the
/// wheel never offers something DropFlow cannot do yet.
/// </summary>
public sealed class WheelBuilder(ToolCatalog catalog, ToolRegistry registry, IEngineProvider engines)
{
    /// <summary>Category key for mixed selections, unknown files and folders (they still get general tools and actions).</summary>
    public const string OtherCategory = "other";

    /// <summary>The input category a selection's pins belong to.</summary>
    public string CategoryKey(IReadOnlyList<string> files)
    {
        var categories = files.Select(f => catalog.CategoryForExtension(ToolCatalog.ExtensionOf(f))).Distinct().ToList();
        return categories is [{ } single] ? single : OtherCategory;
    }

    /// <summary>
    /// The wheels as the user sees them: each wheel's own items, then the items the user pinned to it for this kind of input, then a
    /// "+ Add" slot when anything else applies (an item of another wheel, such as "Extract text (OCR)" on the Convert wheel of a
    /// picture). Pins that do not apply to this selection (no converter, too few or too many files) are kept but not shown.
    /// </summary>
    public WheelSet Build(IReadOnlyList<string> files, string language, IReadOnlyCollection<string> availableActionIds, WheelPins pins)
    {
        var own = Build(files, language, availableActionIds);
        if (files.Count == 0) return own;

        var category = CategoryKey(files);
        var everything = new Dictionary<string, Wedge>(StringComparer.Ordinal);
        foreach (var wedge in own.Convert.Concat(own.Tools).Concat(own.Actions)) everything.TryAdd(wedge.Id, wedge);

        var wheels = new Dictionary<WheelKind, List<Wedge>>();
        var addable = new Dictionary<WheelKind, IReadOnlyList<Wedge>>();
        foreach (var kind in Enum.GetValues<WheelKind>())
        {
            var wedges = own.Of(kind).ToList();
            foreach (var id in pins.For(kind, category))
            {
                if (wedges.Any(w => w.Id == id) || !everything.TryGetValue(id, out var pinned)) continue;
                wedges.Add(pinned with { Pinned = true });
            }

            var onWheel = wedges.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            var rest = everything.Values.Where(w => !onWheel.Contains(w.Id)).ToList();
            addable[kind] = rest;
            if (rest.Count > 0) wedges.Add(new Wedge(Wedge.AddId, kind, "+", ""));
            wheels[kind] = wedges;
        }

        return new WheelSet(wheels[WheelKind.Convert], wheels[WheelKind.Tools], wheels[WheelKind.Actions])
        {
            Category = category,
            Addable = addable,
        };
    }

    /// <summary>Each wheel's own items only (no pins, no "+ Add"): the catalog's view of what the selection supports.</summary>
    public WheelSet Build(IReadOnlyList<string> files, string language, IReadOnlyCollection<string> availableActionIds)
    {
        if (files.Count == 0) return WheelSet.Empty;

        var actions = BuildActions(language, availableActionIds);

        var extensions = files.Select(ToolCatalog.ExtensionOf).ToList();
        var categories = extensions.Select(e => catalog.CategoryForExtension(e)).Distinct().ToList();

        // Mixed or unknown files (and folders) have no conversions, but the general tools (hash, ZIP, rename…) still apply.
        if (categories.Count != 1 || categories[0] is null)
            return new WheelSet([], BuildTools(null, extensions, files.Count, language), actions);

        var category = catalog.Category(categories[0]!)!;
        return new WheelSet(
            BuildConvert(category, extensions),
            BuildTools(category.Id, extensions, files.Count, language),
            actions) { Category = category.Id };
    }

    /// <summary>A readable name for a wedge id (for the Settings list of pinned items): "PDF", "Extract text (OCR)", "Copy to…".</summary>
    public string LabelOf(string id, string language) =>
        Operation.IsConvert(id) ? FormatLabel(Operation.TargetOf(id)) : catalog.Tool(id)?.Name(language) ?? id;

    private List<Wedge> BuildConvert(CatalogCategory category, List<string> extensions)
    {
        var wedges = new List<Wedge>();
        foreach (var target in category.Outputs)
        {
            var op = Operation.Convert(target);
            var converters = extensions.Select(e => registry.Find(op, e)).ToList();
            if (converters.Any(c => c is null)) continue;

            wedges.Add(MakeWedge($"convert:{target}", WheelKind.Convert, FormatLabel(target), op, converters[0]!, multi: false));
        }
        return wedges;
    }

    private List<Wedge> BuildTools(string? categoryId, List<string> extensions, int fileCount, string language)
    {
        var wedges = new List<Wedge>();
        foreach (var tool in catalog.Tools.Where(t => t.Cat == "general" || (categoryId is not null && t.Cat == categoryId)))
        {
            if (!tool.Accepts(fileCount)) continue;
            var converters = extensions.Select(e => registry.Find(tool.Id, e)).ToList();
            if (converters.Any(c => c is null)) continue;

            wedges.Add(MakeWedge(tool.Id, WheelKind.Tools, tool.Name(language), tool.Id, converters[0]!, tool.Multi));
        }
        return wedges;
    }

    private List<Wedge> BuildActions(string language, IReadOnlyCollection<string> available) =>
        catalog.Actions
            .Where(a => available.Contains(a.Id))
            .Select(a => new Wedge(a.Id, WheelKind.Actions, a.Name(language), a.Id))
            .ToList();

    private Wedge MakeWedge(string id, WheelKind wheel, string label, string operation, IConverter converter, bool multi)
    {
        var engine = converter.EngineFor(operation);
        var missing = engine != EngineId.None && engines.Locate(engine) is null;
        return new Wedge(
            id, wheel, label, operation,
            missing ? WedgeState.NeedsEngine : WedgeState.Ready,
            engine,
            converter.OptionsFor(operation).Count > 0,
            multi);
    }

    public static string FormatLabel(string extension) => extension switch
    {
        "pdfa" => "PDF/A",
        "gz" => "GZ",
        _ => extension.ToUpperInvariant(),
    };
}
