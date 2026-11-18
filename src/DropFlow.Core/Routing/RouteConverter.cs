using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;
using DropFlow.Core.Registry;

namespace DropFlow.Core.Routing;

/// <summary>
/// "convert:X" for the catalog's routed targets (an EPUB to PNG goes EPUB → PDF → PNG). It is an ordinary converter, so the queue,
/// history, undo, the CLI and the wheel treat a chain like any conversion. Registered last: it never answers when a direct converter exists.
/// Each file runs on its own: intermediate files go to a private temporary folder, keep the source's name, and are deleted at the end
/// (success, failure or cancel); only the final file lands beside the source as "name (converted).ext".
/// </summary>
public sealed class RouteConverter : IConverter
{
    private readonly ToolCatalog _catalog;
    private readonly RoutePlanner _planner;
    private readonly Func<string, string, IConverter?> _direct;
    private readonly string _tempRoot;

    public RouteConverter(ToolRegistry registry, ToolCatalog catalog, IEngineProvider engines, string? tempRoot = null)
    {
        _catalog = catalog;
        // The planner sees every converter except this one, so a route is never made of routes.
        _direct = (op, ext) => registry.Converters.FirstOrDefault(c => c is not RouteConverter && c.Supports(op, ext));
        var formats = catalog.Categories.SelectMany(c => c.Inputs.Concat(c.Outputs)).Distinct(StringComparer.Ordinal).ToList();
        _planner = new RoutePlanner(_direct, engines, formats);
        _tempRoot = tempRoot ?? ChainRunner.DefaultTempRoot;
    }

    public IReadOnlyCollection<string> Operations =>
        _catalog.Routes.Values.SelectMany(t => t).Distinct().Select(Operation.Convert).ToList();

    public bool Supports(string operation, string inputExtension)
    {
        if (!Operation.IsConvert(operation)) return false;
        var target = Operation.TargetOf(operation);
        var category = _catalog.CategoryForExtension(inputExtension);
        if (category is null || !_catalog.RoutedTargets(category).Contains(target)) return false;
        if (_direct(operation, inputExtension) is not null) return false;
        return Plan(inputExtension, target) is { Steps.Count: >= 2 };
    }

    /// <summary>The chain DropFlow would use, for display ("via PDF") and tests.</summary>
    public Route? Plan(string from, string to) => _planner.Plan(ToolCatalog.Normalize(from), ToolCatalog.Normalize(to));

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var target = Operation.TargetOf(job.Operation);
        var outputs = new List<string>();
        for (var f = 0; f < job.Inputs.Count; f++)
        {
            var input = job.Inputs[f];
            var route = Plan(ToolCatalog.ExtensionOf(input), target);
            if (route is null) return ConversionResult.Fail($"No way to convert {Path.GetFileName(input)} to {target.ToUpperInvariant()}.", started.Elapsed);
            // the user's options belong to the last step (the format they asked for)
            var steps = route.Steps.Select((t, i) => new ChainStep(Operation.Convert(t), i == route.Steps.Count - 1 ? job.Options : null)).ToList();
            var result = await ChainRunner.RunAsync(steps, input, job.OutputDirectory, _direct, progress,
                (double)f / job.Inputs.Count, 1.0 / job.Inputs.Count, _tempRoot, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return result with { Duration = started.Elapsed };
            outputs.AddRange(result.Outputs);
        }
        progress.Report(new JobProgress(1));
        return ConversionResult.Ok(outputs, started.Elapsed);
    }

    /// <summary>Deletes chain folders left behind by a crash (older than a day). Call once at start.</summary>
    public static void SweepLeftovers(string? tempRoot = null) => ChainRunner.SweepLeftovers(tempRoot);
}
