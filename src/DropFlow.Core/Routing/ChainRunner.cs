using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;

namespace DropFlow.Core.Routing;

/// <summary>One step of a chain: an operation and its options (null = the converter's defaults).</summary>
public sealed record ChainStep(string Operation, IReadOnlyDictionary<string, object?>? Options = null);

/// <summary>
/// Runs a chain of operations on ONE file with the direct converters (routes and presets). Intermediate files go to a private temporary
/// folder, keep the source's name (so the final file is "name (converted).ext" or "name (resized).jpg"), and are deleted at the end whatever
/// happens; only the last step writes beside the source (or into <paramref name="outputDirectory"/>). A step that produces nothing
/// ("already small enough") passes its input on, as recipes do.
/// </summary>
public static class ChainRunner
{
    public static string DefaultTempRoot => Path.Combine(Path.GetTempPath(), "DropFlow", "routes");

    public static async Task<ConversionResult> RunAsync(
        IReadOnlyList<ChainStep> steps, string input, string? outputDirectory, Func<string, string, IConverter?> find,
        IProgress<JobProgress> progress, double progressOffset, double progressShare, string tempRoot, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var notes = new List<string>();
        try
        {
            var current = input;
            for (var s = 0; s < steps.Count; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var last = s == steps.Count - 1;
                var step = steps[s];
                var ext = ToolCatalog.ExtensionOf(current);
                if (Operation.IsConvert(step.Operation) && Operation.TargetOf(step.Operation) == ext)
                {
                    // already in that format (a JPG through "resize, then JPG"): nothing to convert
                    if (!last) continue;
                    if (current == input) return ConversionResult.Ok([], started.Elapsed);
                    var same = UniqueBeside(input, outputDirectory, Path.GetExtension(current));
                    File.Copy(current, same);
                    return ConversionResult.Ok([same], started.Elapsed, notes.Count > 0 ? string.Join("; ", notes.Distinct()) : null);
                }
                var converter = find(step.Operation, ext);
                if (converter is null)
                    return ConversionResult.Fail($"Step {s + 1} of {steps.Count} ({Describe(step.Operation)}) cannot run on .{ToolCatalog.ExtensionOf(current)} files.", started.Elapsed);
                var job = new ConversionJob([current], step.Operation, step.Options, last ? outputDirectory ?? Path.GetDirectoryName(input) : work);
                var share = progressShare / steps.Count;
                var offset = progressOffset + s * share;
                var inner = new Progress<JobProgress>(p => progress.Report(new JobProgress(offset + p.Fraction * share, p.Message)));
                var result = await converter.RunAsync(job, inner, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                    return ConversionResult.Fail($"Step {s + 1} of {steps.Count} ({Describe(step.Operation)}) failed: {result.Error ?? "no output"}", started.Elapsed);
                if (result.Note is { } note) notes.Add(note);

                if (result.Outputs.Count == 0)
                {
                    if (!last) continue;                       // nothing to do at this step: pass the file on
                    if (current == input) return ConversionResult.Ok([], started.Elapsed, notes.Count > 0 ? string.Join("; ", notes) : null);
                    // the last step had nothing to do on an intermediate: the intermediate is the result
                    var kept = UniqueBeside(input, outputDirectory, Path.GetExtension(current));
                    File.Copy(current, kept);
                    return ConversionResult.Ok([kept], started.Elapsed, notes.Count > 0 ? string.Join("; ", notes) : null);
                }
                if (last) return ConversionResult.Ok(result.Outputs, started.Elapsed, notes.Count > 0 ? string.Join("; ", notes.Distinct()) : null);

                var produced = result.Outputs[0];
                var renamed = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + Path.GetExtension(produced));
                if (!string.Equals(produced, renamed, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(renamed)) File.Delete(renamed);
                    File.Move(produced, renamed);
                }
                current = renamed;
            }
            return ConversionResult.Fail("The chain has no steps.", started.Elapsed);
        }
        finally
        {
            // intermediate files are ours alone: delete them for good (not to the Recycle Bin)
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept on next start */ }
        }
    }

    private static string Describe(string operation) => Operation.IsConvert(operation) ? Operation.TargetOf(operation).ToUpperInvariant() : operation;

    private static string UniqueBeside(string input, string? outputDirectory, string extension)
    {
        var dir = outputDirectory ?? Path.GetDirectoryName(input)!;
        var stem = Path.GetFileNameWithoutExtension(input);
        var path = Path.Combine(dir, $"{stem} (converted){extension}");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem} (converted {n}){extension}");
        return path;
    }

    /// <summary>Deletes chain folders left behind by a crash (older than a day). Call once at start.</summary>
    public static void SweepLeftovers(string? tempRoot = null)
    {
        var root = tempRoot ?? DefaultTempRoot;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try { if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try again next time */ }
        }
    }
}
