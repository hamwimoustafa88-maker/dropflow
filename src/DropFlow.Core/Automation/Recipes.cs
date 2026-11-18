using System.Text.Json;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Queue;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Automation;

public sealed record RecipeStep(string Operation, IReadOnlyDictionary<string, object?>? Options = null);

/// <summary>
/// A named chain of operations: the outputs of each step become the inputs of the next. A recipe with <paramref name="OnWheel"/> is a
/// <b>preset</b>: it appears on the Tools wheel of its <paramref name="Categories"/> under its <paramref name="Label"/> (Arabic:
/// <paramref name="LabelAr"/>) and runs like any tool ("WhatsApp video", or options the user saved).
/// </summary>
public sealed record Recipe(
    string Name,
    IReadOnlyList<RecipeStep> Steps,
    bool KeepIntermediates = false,
    string? Description = null,
    string? Label = null,
    string? LabelAr = null,
    IReadOnlyList<string>? Categories = null,
    bool OnWheel = false)
{
    /// <summary>Operation id of a preset on the wheel: "preset:whatsapp-video".</summary>
    public string PresetOperation => PresetPrefix + Name;

    public const string PresetPrefix = "preset:";

    public string DisplayName(string language) => (language == "ar" ? LabelAr ?? Label : Label) ?? Name;
}

/// <summary>Runs a recipe through the job queue, one step at a time.</summary>
public sealed class RecipeRunner(JobQueue queue, IFileDisposer disposer)
{
    /// <param name="outputDirectory">Where results go; null = beside each source (the default rule).</param>
    public async Task<ConversionResult> RunAsync(Recipe recipe, IReadOnlyList<string> inputs, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        if (recipe.Steps.Count == 0) return ConversionResult.Fail("The recipe has no steps.");

        var current = inputs;
        var intermediates = new List<string>();
        var notes = new List<string>();
        var started = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < recipe.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = recipe.Steps[i];
            var batch = queue.Enqueue(new ConversionJob(current, step.Operation, step.Options, outputDirectory));
            using var registration = cancellationToken.Register(batch.Cancel);
            var result = await batch.Completion.ConfigureAwait(false);

            if (!result.Success)
                return ConversionResult.Fail($"Step {i + 1} ({step.Operation}) failed: {result.Error}", started.Elapsed);
            if (result.Note is { } note) notes.Add(note);

            // A step that produced nothing ("already optimal", "no location data") is not an error: pass the files through.
            if (result.Outputs.Count > 0)
            {
                if (i > 0) intermediates.AddRange(current.Where(f => !inputs.Contains(f)));
                current = result.Outputs;
            }
        }

        if (!recipe.KeepIntermediates)
            foreach (var path in intermediates.Distinct().Where(p => !current.Contains(p)))
            {
                try { disposer.Dispose(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leaving an intermediate behind is harmless */ }
            }

        var finalOutputs = current.Where(f => !inputs.Contains(f)).ToList();
        return ConversionResult.Ok(finalOutputs, started.Elapsed, notes.Count == 0 ? null : string.Join("; ", notes.Distinct()));
    }
}

/// <summary>Reads and writes recipes as JSON. Option values come back as plain numbers/text/bools (not JsonElement) so converters can read them.</summary>
public sealed class RecipeStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public IReadOnlyList<Recipe> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<Recipe>>(File.ReadAllText(path), Json) ?? [];
            return raw.Select(r => r with { Steps = r.Steps.Select(s => s with { Options = Normalize(s.Options) }).ToList() }).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<Recipe> recipes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(recipes.ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>JsonElement values (what System.Text.Json produces for <c>object</c>) become string/double/bool.</summary>
    public static IReadOnlyDictionary<string, object?>? Normalize(IReadOnlyDictionary<string, object?>? options) =>
        options?.ToDictionary(kv => kv.Key, kv => kv.Value is JsonElement e ? e.ValueKind switch
        {
            JsonValueKind.String => (object?)e.GetString(),
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : kv.Value);
}

/// <summary>Ready-made recipes. Only the ones whose every step is implemented are offered.</summary>
public static class BuiltInRecipes
{
    public static readonly IReadOnlyList<Recipe> All =
    [
        new("web-photo", [new("img.strip-gps"), new("img.resize", new Dictionary<string, object?> { ["width"] = 1920 }), new("convert:webp", new Dictionary<string, object?> { ["quality"] = 82 })],
            Description: "Remove location, fit 1920 px wide, save as WebP."),
        new("share-safe-photo", [new("img.strip-gps"), new("img.compress", new Dictionary<string, object?> { ["mode"] = "preset", ["preset"] = "balanced" })],
            Description: "Remove location and shrink for sharing."),
        new("pdf-light", [new("pdf.compress", new Dictionary<string, object?> { ["level"] = "recommended" })],
            Description: "Make PDFs smaller."),
        new("heic-to-jpg", [new("convert:jpg", new Dictionary<string, object?> { ["quality"] = 90 })],
            Description: "Convert phone photos to JPG."),
        // ready-made presets on the Tools wheel (sizes as the services accept them in 2026)
        new("whatsapp-video", [new("vid.target-size", new Dictionary<string, object?> { ["sizeMb"] = 16d })],
            Description: "A video small enough to send on WhatsApp (16 MB).", Label: "WhatsApp video (16 MB)", LabelAr: "فيديو واتساب (16MB)",
            Categories: ["video"], OnWheel: true),
        new("discord-video", [new("vid.target-size", new Dictionary<string, object?> { ["sizeMb"] = 10d })],
            Description: "A video small enough for Discord without Nitro (10 MB).", Label: "Discord video (10 MB)", LabelAr: "فيديو ديسكورد (10MB)",
            Categories: ["video"], OnWheel: true),
        new("email-video", [new("vid.target-size", new Dictionary<string, object?> { ["sizeMb"] = 18d })],
            Description: "A video that fits in an e-mail attachment (18 MB).", Label: "E-mail video (18 MB)", LabelAr: "فيديو للبريد (18MB)",
            Categories: ["video"], OnWheel: true),
        new("instagram-photo", [new("img.resize", new Dictionary<string, object?> { ["width"] = 1080d }), new("convert:jpg", new Dictionary<string, object?> { ["quality"] = 90d })],
            Description: "1080 px wide JPG, the size Instagram shows.", Label: "Instagram photo (1080 px)", LabelAr: "صورة إنستغرام (1080px)",
            Categories: ["image"], OnWheel: true),
    ];

    /// <summary>The recipes whose steps all have a converter for at least one extension DropFlow can see.</summary>
    public static IEnumerable<Recipe> Available(ToolRegistry registry) =>
        All.Where(r => r.Steps.All(s => registry.ImplementedOperations.Contains(s.Operation)));
}
