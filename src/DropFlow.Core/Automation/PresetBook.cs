using System.Text.Json;
using System.Text.RegularExpressions;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Automation;

/// <summary>
/// The user's own presets: made from the options of one run ("Save as a preset"), kept in recipes.json, and shared as a small local file
/// (<c>.dropflow-presets.json</c>). Nothing here touches the network; sharing is the user's file to send. An imported file is checked
/// against an allow-list before anything is kept: only operations DropFlow has, only option keys those operations declare, values within
/// their limits, no passwords, no folders and nothing that looks like a path. Importing never runs anything.
/// </summary>
public static partial class PresetBook
{
    public const string FileFormat = "dropflow-presets";
    public const int FormatVersion = 1;
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxPresets = 200;
    public const int MaxSteps = 10;
    public const int MaxText = 256;

    /// <summary>A preset from one operation's options (passwords and folders are left out).</summary>
    public static Recipe FromOptions(string operation, IReadOnlyList<ToolOption> options, IReadOnlyDictionary<string, object?> values, string label, ToolCatalog catalog, IEnumerable<Recipe> existing)
    {
        var secret = options.Where(o => o.Kind is ToolOptionKind.Password or ToolOptionKind.Folder).Select(o => o.Key).ToHashSet();
        var kept = values.Where(v => !secret.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);
        var name = UniqueName(Slug(label), existing.Select(r => r.Name));
        return new Recipe(name, [new RecipeStep(operation, kept)], Label: label, LabelAr: label, Categories: CategoriesOf(operation, catalog), OnWheel: true);
    }

    /// <summary>The input categories whose wheel can start with this operation.</summary>
    public static IReadOnlyList<string> CategoriesOf(string operation, ToolCatalog catalog)
    {
        if (Operation.IsConvert(operation))
            return catalog.Categories.Where(c => c.Outputs.Contains(Operation.TargetOf(operation)) || catalog.RoutedTargets(c.Id).Contains(Operation.TargetOf(operation))).Select(c => c.Id).ToList();
        var tool = catalog.Tool(operation);
        if (tool is null || tool.Cat is null or "general") return [];
        return [tool.Cat, .. tool.AlsoIn ?? []];
    }

    public static string Export(IEnumerable<Recipe> presets) => JsonSerializer.Serialize(new
    {
        format = FileFormat,
        version = FormatVersion,
        presets = presets.Where(p => p.OnWheel).Select(p => new
        {
            name = p.Name, label = p.Label, labelAr = p.LabelAr, categories = p.Categories,
            steps = p.Steps.Select(s => new { operation = s.Operation, options = s.Options }),
        }),
    }, new JsonSerializerOptions { WriteIndented = true });

    public sealed record ImportResult(IReadOnlyList<Recipe> Presets, IReadOnlyList<string> Rejected);

    /// <summary>Checks a shared file and returns the presets that pass (with fresh names where they clash) and why the others did not.</summary>
    public static ImportResult Import(string json, ToolCatalog catalog, ToolRegistry registry, IEnumerable<Recipe> existing)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes) return new([], ["file-too-large"]);
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return new([], ["not-json"]); }
        if (root.ValueKind != JsonValueKind.Object || Str(root, "format") != FileFormat || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != FormatVersion)
            return new([], ["not-a-preset-file"]);
        if (!root.TryGetProperty("presets", out var list) || list.ValueKind != JsonValueKind.Array) return new([], ["no-presets"]);

        var taken = existing.Select(r => r.Name).ToList();
        var ok = new List<Recipe>();
        var rejected = new List<string>();
        foreach (var p in list.EnumerateArray().Take(MaxPresets))
        {
            var label = Clean(Str(p, "label")) ?? Clean(Str(p, "name"));
            if (label is null) { rejected.Add("(no name): missing name"); continue; }
            var problem = CheckSteps(p, catalog, registry, out var steps);
            if (problem is not null) { rejected.Add($"{label}: {problem}"); continue; }
            var categories = p.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                ? cats.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null).Where(c => c is not null && catalog.Category(c) is not null).Select(c => c!).Distinct().ToList()
                : [];
            var name = UniqueName(Slug(Str(p, "name") ?? label), taken);
            taken.Add(name);
            ok.Add(new Recipe(name, steps, Label: label, LabelAr: Clean(Str(p, "labelAr")) ?? label, Categories: categories.Count > 0 ? categories : CategoriesOf(steps[0].Operation, catalog), OnWheel: true));
        }
        if (list.GetArrayLength() > MaxPresets) rejected.Add($"only the first {MaxPresets} presets were read");
        return new(ok, rejected);
    }

    private static string? CheckSteps(JsonElement preset, ToolCatalog catalog, ToolRegistry registry, out List<RecipeStep> steps)
    {
        steps = [];
        if (!preset.TryGetProperty("steps", out var raw) || raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() == 0) return "no steps";
        if (raw.GetArrayLength() > MaxSteps) return $"more than {MaxSteps} steps";
        foreach (var s in raw.EnumerateArray())
        {
            var op = Str(s, "operation");
            if (op is null || op.StartsWith(Recipe.PresetPrefix, StringComparison.Ordinal)) return "a step has no valid operation";
            var known = Operation.IsConvert(op) ? catalog.Categories.Any(c => c.Outputs.Contains(Operation.TargetOf(op))) : catalog.Tool(op) is not null;
            var converter = registry.Converters.FirstOrDefault(c => c.Operations.Contains(op));
            if (!known || converter is null) return $"DropFlow has no '{op}'";
            var declared = converter.OptionsFor(op).ToDictionary(o => o.Key);
            var options = new Dictionary<string, object?>();
            if (s.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
            {
                foreach (var o in opts.EnumerateObject())
                {
                    if (!declared.TryGetValue(o.Name, out var spec)) return $"'{op}' has no option '{o.Name}'";
                    if (spec.Kind is ToolOptionKind.Password or ToolOptionKind.Folder) continue;   // never imported
                    var value = Value(o.Value, spec, out var why);
                    if (why is not null) return $"{o.Name}: {why}";
                    options[o.Name] = value;
                }
            }
            steps.Add(new RecipeStep(op, options));
        }
        return null;
    }

    private static object? Value(JsonElement e, ToolOption spec, out string? problem)
    {
        problem = null;
        switch (e.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                return e.GetBoolean();
            case JsonValueKind.Number:
                var d = e.GetDouble();
                return Math.Clamp(d, spec.Min ?? double.MinValue, spec.Max ?? double.MaxValue);
            case JsonValueKind.String:
                var text = e.GetString() ?? "";
                if (text.Length > MaxText) { problem = "text too long"; return null; }
                if (LooksLikePath(text)) { problem = "looks like a file path"; return null; }
                if (spec.Kind == ToolOptionKind.Choice && spec.Choices is { } choices && !choices.Contains(text)) { problem = "not one of the choices"; return null; }
                return text;
            case JsonValueKind.Null:
                return null;
            default:
                problem = "unsupported value";
                return null;
        }
    }

    internal static bool LooksLikePath(string text) =>
        Path.IsPathRooted(text) || text.StartsWith(@"\\", StringComparison.Ordinal) || text.Contains("..", StringComparison.Ordinal) || text.Contains('%') || PathLike().IsMatch(text);

    [GeneratedRegex(@"^[A-Za-z]:|[\\/]")]
    private static partial Regex PathLike();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length == 0 ? null : t.Length > 60 ? t[..60] : t;
    }

    public static string Slug(string label)
    {
        var slug = new string(label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "preset" : slug.Length > 40 ? slug[..40] : slug;
    }

    private static string UniqueName(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // never shadow a built-in either
        foreach (var b in BuiltInRecipes.All) used.Add(b.Name);
        if (!used.Contains(name)) return name;
        for (var n = 2; ; n++) if (!used.Contains($"{name}-{n}")) return $"{name}-{n}";
    }
}
