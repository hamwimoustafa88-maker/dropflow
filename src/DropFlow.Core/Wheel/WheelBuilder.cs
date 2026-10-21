using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Wheel;

public enum WheelKind { Convert, Tools, Actions }

public enum WedgeState { Ready, NeedsEngine }

/// <summary>One slice of a wheel.</summary>
public sealed record Wedge(
    string Id,
    WheelKind Wheel,
    string Label,
    string Operation,
    WedgeState State = WedgeState.Ready,
    EngineId Engine = EngineId.None,
    bool NeedsOptions = false,
    bool Multi = false);

public sealed record WheelSet(IReadOnlyList<Wedge> Convert, IReadOnlyList<Wedge> Tools, IReadOnlyList<Wedge> Actions)
{
    public static readonly WheelSet Empty = new([], [], []);

    public IReadOnlyList<Wedge> Of(WheelKind kind) => kind switch
    {
        WheelKind.Convert => Convert,
        WheelKind.Tools => Tools,
        _ => Actions,
    };
}

/// <summary>
/// Decides what each wheel shows for a selection of files. Only operations that have a registered converter appear, so the
/// wheel never offers something DropFlow cannot do yet.
/// </summary>
public sealed class WheelBuilder(ToolCatalog catalog, ToolRegistry registry, IEngineProvider engines)
{
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
            actions);
    }

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
            if (fileCount < tool.MinimumFiles) continue;
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
