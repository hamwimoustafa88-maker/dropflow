using DropFlow.Core.Operations;

namespace DropFlow.Engines.Media.Runtime;

/// <summary>
/// One tool (or the family of format conversions) implemented with FFmpeg. Subclasses describe what they accept and run the work;
/// <see cref="MediaOperationConverter"/> adds the shared behaviour: engine check, input check, error mapping, cancellation cleanup.
/// </summary>
internal abstract class MediaOperation
{
    /// <summary>Operation ids this class implements (<c>vid.trim</c>, or every <c>convert:xyz</c>).</summary>
    public abstract IReadOnlyCollection<string> Ids { get; }

    /// <summary>True for exactly the operation/extension pairs that are really implemented (extension without dot, lower-case).</summary>
    public abstract bool Supports(string operation, string inputExtension);

    /// <summary>Options to ask the user for; empty runs immediately with defaults.</summary>
    public virtual IReadOnlyList<ToolOption> OptionsFor(string operation) => [];

    /// <summary>True for tools that receive every selected file in one job (join, merge).</summary>
    public virtual bool IsMulti => false;

    /// <summary>Runs the work and returns the produced paths (files, or a folder). Throw <see cref="MediaFailure"/> for expected failures.</summary>
    public abstract Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx);
}

/// <summary>Adapts a <see cref="MediaOperation"/> to <see cref="IConverter"/>.</summary>
internal sealed class MediaOperationConverter(MediaOperation operation, MediaServices services) : IConverter
{
    public IReadOnlyCollection<string> Operations => operation.Ids;

    public bool Supports(string op, string inputExtension) =>
        operation.Ids.Contains(op) && operation.Supports(op, inputExtension.TrimStart('.').ToLowerInvariant());

    public EngineId EngineFor(string op) => EngineId.Ffmpeg;

    public IReadOnlyList<ToolOption> OptionsFor(string op) => operation.Ids.Contains(op) ? operation.OptionsFor(op) : [];

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        TimeSpan Elapsed() => DateTime.UtcNow - started;

        if (job.Inputs.Count == 0) return ConversionResult.Fail("No input file was given.");
        if (!operation.Ids.Contains(job.Operation))
            return ConversionResult.Fail($"This converter does not implement '{job.Operation}'.");
        if (services.Runner.FfmpegPath is null)
            return ConversionResult.Fail("FFmpeg is not installed. Install the FFmpeg engine from DropFlow's engine installer (or run: winget install Gyan.FFmpeg).");
        if (operation.IsMulti && job.Inputs.Count < 2)
            return ConversionResult.Fail("Select at least two files.");
        foreach (var input in job.Inputs)
            if (!File.Exists(input)) return ConversionResult.Fail($"Input file not found: {input}");
        if (!operation.Supports(job.Operation, Path.GetExtension(job.Inputs[0]).TrimStart('.').ToLowerInvariant()))
            return ConversionResult.Fail($"'{job.Operation}' does not support .{Path.GetExtension(job.Inputs[0]).TrimStart('.')} files.");

        var ctx = new MediaContext(job, services, progress, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new JobProgress(0));
            var outputs = await operation.ExecuteAsync(ctx).ConfigureAwait(false);
            progress.Report(new JobProgress(1));
            return ConversionResult.Ok(outputs, Elapsed(), ctx.Note);
        }
        catch (OperationCanceledException)
        {
            ctx.CleanupOutputs();
            throw;
        }
        catch (MediaFailure failure)
        {
            ctx.CleanupOutputs();
            return ConversionResult.Fail(failure.Message, Elapsed());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ctx.CleanupOutputs();
            return ConversionResult.Fail($"A file or program could not be accessed: {ex.Message}", Elapsed());
        }
        finally
        {
            ctx.CleanupTemp();
        }
    }
}
