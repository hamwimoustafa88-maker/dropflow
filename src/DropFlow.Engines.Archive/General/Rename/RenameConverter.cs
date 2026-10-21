using System.Diagnostics;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Archive.Archives;

namespace DropFlow.Engines.Archive.General.Rename;

/// <summary>
/// <c>gen.rename</c> (multi-file). DropFlow never overwrites or deletes originals, so the default <c>mode=copy</c> writes
/// renamed COPIES into a new folder <c>first-name (renamed)</c> and leaves the originals alone. <c>mode=rename</c> renames the
/// originals in place, only when the user asked for it: it refuses to run if any new name is taken or duplicated, never overwrites,
/// and rolls back on failure. In rename mode the result lists no outputs (so "undo = delete outputs" can never delete a user's
/// file); use <see cref="BatchRename.Reverse"/> with the moves from <see cref="BatchRename.Apply"/> to undo.
/// <para>Option keys: pattern, find, replace, regex, ignoreCase, findInExtension, trim, trimChars, collapseSpaces, removeFirst,
/// removeLast, insert, insertAt, insertFromEnd, case, lowerExtension, start, step, dateSource, dateFormat, sort
/// (<c>name</c> natural order, default; <c>modified</c>; <c>none</c> keeps the given order), mode (<c>copy</c>|<c>rename</c>).</para>
/// </summary>
public sealed class RenameConverter : IConverter
{
    public const string OperationId = "gen.rename";

    private static readonly IReadOnlyList<ToolOption> Options =
    [
        new("pattern", ToolOptionKind.Text, "{name}{ext}"),
        new("find", ToolOptionKind.Text, ""),
        new("replace", ToolOptionKind.Text, ""),
        new("regex", ToolOptionKind.Bool, false),
        new("ignoreCase", ToolOptionKind.Bool, false),
        new("case", ToolOptionKind.Choice, "none", ["none", "lower", "upper", "title", "sentence"]),
        new("trim", ToolOptionKind.Bool, false),
        new("insert", ToolOptionKind.Text, ""),
        new("insertAt", ToolOptionKind.Number, 0, null, 0, 255),
        new("start", ToolOptionKind.Number, 1, null, 0, 1_000_000),
        new("mode", ToolOptionKind.Choice, "copy", ["copy", "rename"]),
    ];

    public IReadOnlyCollection<string> Operations { get; } = [OperationId];
    public bool Supports(string operation, string inputExtension) => operation == OperationId;
    public EngineId EngineFor(string operation) => EngineId.None;
    public IReadOnlyList<ToolOption> OptionsFor(string operation) => Options;

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Run(job, progress, cancellationToken), cancellationToken);
    }

    private static ConversionResult Run(ConversionJob job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input files.");
        foreach (var input in job.Inputs)
            if (!File.Exists(input) && !Directory.Exists(input))
                return ConversionResult.Fail($"Not found: {input}");

        var mode = (job.GetString("mode", "copy") ?? "copy").Trim().ToLowerInvariant();
        if (mode is not ("copy" or "rename")) return ConversionResult.Fail($"Unknown mode '{mode}'. Use copy or rename.");

        RenameSpec spec;
        try { spec = RenameSpec.FromJob(job); }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            return ConversionResult.Fail($"An option has an invalid value: {ex.Message}");
        }

        var sw = Stopwatch.StartNew();
        var ordered = Sort(job.Inputs, job.GetString("sort", "name") ?? "name");

        try
        {
            return mode == "copy"
                ? Copy(job, ordered, spec, progress, ct, sw)
                : RenameInPlace(ordered, spec, ct, sw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConversionResult.Fail($"Renaming failed: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> Sort(IReadOnlyList<string> inputs, string how) => how.ToLowerInvariant() switch
    {
        "none" => inputs,
        "modified" => inputs.OrderBy(p => File.Exists(p) ? File.GetLastWriteTime(p) : Directory.GetLastWriteTime(p)).ToList(),
        _ => inputs.OrderBy(p => Path.GetFileName(p), NaturalComparer.Instance).ToList(),
    };

    private static ConversionResult Copy(ConversionJob job, IReadOnlyList<string> inputs, RenameSpec spec, IProgress<JobProgress> progress, CancellationToken ct, Stopwatch sw)
    {
        if (inputs.Any(Directory.Exists))
            return ConversionResult.Fail("Folders can only be renamed in place (mode=rename). Select files to make renamed copies.");

        var folder = new OutputPathResolver().ForFolder(OutputNaming.BaseOfAny(job.Inputs[0]), "renamed", job.OutputDirectory);
        var plan = BatchRename.Plan(inputs, spec, new RenamePlanOptions { DestinationDirectory = folder, Conflicts = ConflictPolicy.Uniquify });
        if (plan.HasConflicts) return ConversionResult.Fail($"Cannot rename: {plan.Describe()}");

        using var created = new CreatedOutputs();
        Directory.CreateDirectory(folder);
        created.AddFolder(folder);

        for (var i = 0; i < plan.Items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = plan.Items[i];
            CopyFile(item.SourcePath, item.TargetPath, ct);
            progress.Report(new JobProgress((i + 1.0) / plan.Items.Count, item.NewName));
        }

        created.Commit();
        var adjusted = plan.Items.Count(i => i.Adjusted);
        return ConversionResult.Ok([folder], sw.Elapsed,
            adjusted > 0 ? $"{adjusted} name(s) were already taken and got a number added" : null);
    }

    private static ConversionResult RenameInPlace(IReadOnlyList<string> inputs, RenameSpec spec, CancellationToken ct, Stopwatch sw)
    {
        var plan = BatchRename.Plan(inputs, spec, new RenamePlanOptions { Conflicts = ConflictPolicy.Report });
        if (plan.HasConflicts) return ConversionResult.Fail($"Cannot rename, nothing was changed: {plan.Describe()}");
        if (plan.ChangeCount == 0) return ConversionResult.Ok([], sw.Elapsed, "All names already match; nothing to rename.");

        var result = BatchRename.Apply(plan, ct);
        if (!result.Success) return ConversionResult.Fail(result.Error!);

        // No outputs on purpose: these are the user's own files, and "undo" must never delete them.
        return ConversionResult.Ok([], sw.Elapsed, $"Renamed {result.Moves.Count} file(s) in place.");
    }

    private static void CopyFile(string source, string target, CancellationToken ct)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }

        try { File.SetLastWriteTime(target, File.GetLastWriteTime(source)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cosmetic */ }
    }
}
