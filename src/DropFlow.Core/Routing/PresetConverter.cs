using DropFlow.Core.Automation;
using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Routing;

/// <summary>
/// Runs presets ("preset:whatsapp-video"): recipes marked <see cref="Recipe.OnWheel"/>, built in or saved by the user. A preset is offered for
/// a file when the file's category is one of the preset's and the first step can run on it. Each file goes through the steps on its own
/// (<see cref="ChainRunner"/>), so only the final file lands beside the source. Being an ordinary converter, a preset gets the queue,
/// history, undo, the progress ring and the CLI (<c>dropflow run preset:whatsapp-video clip.mp4</c>) for free.
/// </summary>
public sealed class PresetConverter : IConverter
{
    private readonly Func<IReadOnlyList<Recipe>> _presets;
    private readonly ToolCatalog _catalog;
    private readonly Func<string, string, IConverter?> _direct;
    private readonly string _tempRoot;

    public PresetConverter(ToolRegistry registry, ToolCatalog catalog, Func<IReadOnlyList<Recipe>> presets, string? tempRoot = null)
    {
        _presets = presets;
        _catalog = catalog;
        // presets are made of ordinary tools and conversions (routes included), never of other presets
        _direct = (op, ext) => registry.Converters.FirstOrDefault(c => c is not PresetConverter && c.Supports(op, ext));
        _tempRoot = tempRoot ?? ChainRunner.DefaultTempRoot;
    }

    public IReadOnlyCollection<string> Operations => _presets().Where(p => p.OnWheel).Select(p => p.PresetOperation).ToList();

    public Recipe? Find(string operation) =>
        operation.StartsWith(Recipe.PresetPrefix, StringComparison.Ordinal)
            ? _presets().FirstOrDefault(p => p.OnWheel && p.PresetOperation == operation)
            : null;

    public bool Supports(string operation, string inputExtension)
    {
        if (Find(operation) is not { Steps.Count: > 0 } preset) return false;
        var ext = ToolCatalog.Normalize(inputExtension);
        if (preset.Categories is { Count: > 0 } cats && !cats.Contains(_catalog.CategoryForExtension(ext) ?? "")) return false;
        var first = preset.Steps[0].Operation;
        return _direct(first, ext) is not null || (Operation.IsConvert(first) && Operation.TargetOf(first) == ext);
    }

    /// <summary>The engine of the first step (the wheel marks the slice when it is missing).</summary>
    public EngineId EngineFor(string operation) =>
        Find(operation)?.Steps.Select(s => _direct(s.Operation, "") ?? FirstSupporting(s.Operation)).FirstOrDefault(c => c is not null)?.EngineFor(operation) ?? EngineId.None;

    private IConverter? FirstSupporting(string operation) =>
        _catalog.Categories.SelectMany(c => c.Inputs).Select(e => _direct(operation, e)).FirstOrDefault(c => c is not null);

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (Find(job.Operation) is not { } preset) return ConversionResult.Fail($"Unknown preset '{job.Operation}'.");
        var steps = preset.Steps.Select(s => new ChainStep(s.Operation, s.Options)).ToList();
        var outputs = new List<string>();
        var notes = new List<string>();
        for (var f = 0; f < job.Inputs.Count; f++)
        {
            var result = await ChainRunner.RunAsync(steps, job.Inputs[f], job.OutputDirectory, _direct, progress,
                (double)f / job.Inputs.Count, 1.0 / job.Inputs.Count, _tempRoot, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return result with { Duration = started.Elapsed };
            outputs.AddRange(result.Outputs);
            if (result.Note is { } n) notes.Add(n);
        }
        progress.Report(new JobProgress(1));
        return ConversionResult.Ok(outputs, started.Elapsed, notes.Count > 0 ? string.Join("; ", notes.Distinct()) : null);
    }
}
