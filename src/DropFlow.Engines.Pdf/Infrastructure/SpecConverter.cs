using DropFlow.Core.Catalog;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Pdf.Infrastructure;

/// <summary>An expected, user-facing failure (wrong password, missing engine, nothing to do). Becomes <c>ConversionResult.Fail</c>.</summary>
internal sealed class ConversionFailure(string message) : Exception(message);

internal delegate Task<ConversionResult> OperationRun(JobContext context);

/// <summary>One (operation, input extensions) pair a converter implements, with the options the UI should ask for.</summary>
internal sealed record OperationSpec(
    string Operation,
    IReadOnlyCollection<string> Inputs,
    OperationRun Run,
    IReadOnlyList<ToolOption>? Options = null)
{
    public static OperationSpec For(string operation, string input, OperationRun run, IReadOnlyList<ToolOption>? options = null) =>
        new(operation, [input], run, options);
}

/// <summary>
/// A table-driven <see cref="IConverter"/>: every converter in this module is "a list of specs plus one engine". Keeping the engine
/// per converter (not per spec) is what makes <see cref="EngineFor"/> truthful - managed paths and LibreOffice paths for the same
/// operation (for example <c>convert:docx</c>) live in different converter instances.
/// </summary>
internal sealed class SpecConverter : IConverter
{
    private readonly EngineId _engine;
    private readonly IReadOnlyList<OperationSpec> _specs;

    public SpecConverter(EngineId engine, IEnumerable<OperationSpec> specs)
    {
        _engine = engine;
        _specs = specs.ToList();
        Operations = _specs.Select(s => s.Operation).Distinct().ToList();
    }

    public IReadOnlyCollection<string> Operations { get; }

    public IReadOnlyList<OperationSpec> Specs => _specs;

    public bool Supports(string operation, string inputExtension) => Find(operation, inputExtension) is not null;

    public EngineId EngineFor(string operation) => _engine;

    public IReadOnlyList<ToolOption> OptionsFor(string operation) =>
        _specs.FirstOrDefault(s => s.Operation == operation)?.Options ?? [];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file was given.");

        var spec = Find(job.Operation, ToolCatalog.ExtensionOf(job.Inputs[0]));
        if (spec is null)
            return ConversionResult.Fail($"'{job.Operation}' is not supported for .{ToolCatalog.ExtensionOf(job.Inputs[0])} files.");

        foreach (var input in job.Inputs)
        {
            if (!File.Exists(input))
                return ConversionResult.Fail($"File not found: {Path.GetFileName(input)}");
        }

        using var context = new JobContext(job, progress, cancellationToken);
        try
        {
            var result = await Task.Run(() => spec.Run(context), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Success) context.Commit();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConversionFailure failure)
        {
            return ConversionResult.Fail(failure.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Libraries report corrupt or unsupported files with many exception types; for the user that is one expected failure.
            return ConversionResult.Fail($"{Path.GetFileName(job.Inputs[0])}: {ex.Message}");
        }
    }

    private OperationSpec? Find(string operation, string inputExtension)
    {
        var ext = inputExtension.TrimStart('.').ToLowerInvariant();
        return _specs.FirstOrDefault(s => s.Operation == operation && s.Inputs.Contains(ext));
    }
}
