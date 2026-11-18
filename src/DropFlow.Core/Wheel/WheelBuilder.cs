using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;
using DropFlow.Core.Settings;

namespace DropFlow.Core.Wheel;

public enum WheelKind { Convert, Tools, Actions }

public enum WedgeState { Ready, NeedsEngine }

/// <summary>A wheel has a front and, when it holds more items than fit, a back (turned over with Tab or the mouse wheel).</summary>
public enum WheelFace { Front, Back }

/// <summary>The two faces of one wheel.</summary>
public sealed record WheelFaces(IReadOnlyList<Wedge> Front, IReadOnlyList<Wedge> Back)
{
    /// <summary>Default number of slices on the front of a wheel (Settings can change it).</summary>
    public const int DefaultFrontMax = 10;
    public const int MinFrontMax = 4;
    public const int MaxFrontMax = 16;

    public bool HasBack => Back.Count > 0;

    public IReadOnlyList<Wedge> Of(WheelFace face) => face == WheelFace.Back ? Back : Front;

    /// <summary>
    /// The first <paramref name="frontMax"/> items stay in front in their order; the rest (and so the "+ Add" slot, always last) go to the
    /// back. A wheel that fits has no back.
    /// </summary>
    public static WheelFaces Split(IReadOnlyList<Wedge> items, int frontMax)
    {
        var max = Math.Clamp(frontMax, 1, MaxFrontMax);
        return items.Count <= max ? new WheelFaces(items, []) : new WheelFaces(items.Take(max).ToList(), items.Skip(max).ToList());
    }
}

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

    /// <summary>Presets shown at the end of the Tools wheel (built-in templates and the user's saved options). Null = none.</summary>
    public Func<IReadOnlyList<Automation.Recipe>>? Presets { get; init; }

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
    public WheelSet Build(IReadOnlyList<string> files, string language, IReadOnlyCollection<string> availableActionIds, WheelPins pins, WheelLayouts? layouts = null)
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
            var wedges = WithPins(own.Of(kind), kind, category, pins, everything);
            // the user's arrangement: their order, and hidden items out of the wheel (they stay reachable through "+ Add")
            wedges = (layouts ?? WheelLayouts.None).For(kind, category).Apply(wedges).ToList();

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

    private static List<Wedge> WithPins(IReadOnlyList<Wedge> own, WheelKind kind, string category, WheelPins pins, IReadOnlyDictionary<string, Wedge> everything)
    {
        var wedges = own.ToList();
        foreach (var id in pins.For(kind, category))
        {
            if (wedges.Any(w => w.Id == id) || !everything.TryGetValue(id, out var pinned)) continue;
            wedges.Add(pinned with { Pinned = true });
        }
        return wedges;
    }

    /// <summary>
    /// Everything that can be arranged on one wheel for one kind of input (Settings → Arrange the wheels): the wheel's own items for a typical
    /// file of that category plus the items pinned to it, in their default order, without "+ Add".
    /// </summary>
    public IReadOnlyList<Wedge> ArrangeableFor(WheelKind kind, string category, string language, IReadOnlyCollection<string> availableActionIds, WheelPins pins)
    {
        // Every input of the category contributes (a JPG has no "JPG" wedge, a PNG does): the union, in catalog order.
        var samples = catalog.Category(category)?.Inputs.Select(e => $"sample.{e}").ToList() ?? ["sample"];
        var everything = new Dictionary<string, Wedge>(StringComparer.Ordinal);
        var onWheel = new Dictionary<string, Wedge>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var own = Build([sample], language, availableActionIds);
            foreach (var wedge in own.Convert.Concat(own.Tools).Concat(own.Actions)) everything.TryAdd(wedge.Id, wedge);
            foreach (var wedge in own.Of(kind)) onWheel.TryAdd(wedge.Id, wedge);
        }
        var outputs = catalog.Category(category)?.Outputs.ToList() ?? [];
        var tools = catalog.Tools.Select(t => t.Id).ToList();
        int Rank(Wedge w) => Operation.IsConvert(w.Id) ? outputs.IndexOf(Operation.TargetOf(w.Id)) : 1000 + tools.IndexOf(w.Id);
        var ordered = kind == WheelKind.Actions ? onWheel.Values.ToList() : onWheel.Values.OrderBy(Rank).ToList();
        return WithPins(ordered, kind, category, pins, everything);
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
        // direct outputs, then the targets DropFlow reaches by chaining conversions (catalog "routes")
        foreach (var target in category.Outputs.Concat(catalog.RoutedTargets(category.Id)))
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
        foreach (var tool in catalog.Tools.Where(t => t.Cat == "general" || t.AppliesTo(categoryId)))
        {
            if (!tool.Accepts(fileCount)) continue;
            var converters = extensions.Select(e => registry.Find(tool.Id, e)).ToList();
            if (converters.Any(c => c is null)) continue;

            wedges.Add(MakeWedge(tool.Id, WheelKind.Tools, tool.Name(language), tool.Id, converters[0]!, tool.Multi));
        }

        foreach (var preset in Presets?.Invoke().Where(p => p.OnWheel) ?? [])
        {
            if (preset.Categories is { Count: > 0 } cats && (categoryId is null || !cats.Contains(categoryId))) continue;
            var op = preset.PresetOperation;
            var converters = extensions.Select(e => registry.Find(op, e)).ToList();
            if (converters.Count == 0 || converters.Any(c => c is null)) continue;
            wedges.Add(MakeWedge(op, WheelKind.Tools, preset.DisplayName(language), op, converters[0]!, multi: false));
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
