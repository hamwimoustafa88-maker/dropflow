using DropFlow.Core.Operations;

namespace DropFlow.Engines.Images;

/// <summary>
/// Base for the single-file image tools: runs the work off the calling thread, maps expected failures to
/// <see cref="ConversionResult.Fail"/>, and guarantees cancellation is observed before any output is written.
/// </summary>
internal abstract class ImageTool(params string[] operations) : IConverter
{
    public IReadOnlyCollection<string> Operations { get; } = operations;

    public virtual bool Supports(string operation, string inputExtension) =>
        Operations.Contains(operation) && ImageIo.Inputs.Contains(inputExtension);

    public virtual IReadOnlyList<ToolOption> OptionsFor(string operation) => [];

    public Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var outputs = new List<string>();
            string? note = null;
            try
            {
                for (var i = 0; i < job.Inputs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(new JobProgress((double)i / job.Inputs.Count));
                    var produced = Process(job, job.Inputs[i], cancellationToken, out var n);
                    outputs.AddRange(produced);
                    note ??= n;
                }
                progress.Report(new JobProgress(1));
                return ConversionResult.Ok(outputs, started.Elapsed, note);
            }
            catch (OperationCanceledException)
            {
                Cleanup(outputs);
                throw;
            }
            catch (ImageMagick.MagickException ex)
            {
                Cleanup(outputs);
                return ConversionResult.Fail($"{Path.GetFileName(job.Inputs.FirstOrDefault() ?? "")}: {ex.Message}", started.Elapsed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidDataException)
            {
                Cleanup(outputs);
                return ConversionResult.Fail(ex.Message, started.Elapsed);
            }
        }, CancellationToken.None);

    /// <summary>Handles one input. Returns the files it created. Set <paramref name="note"/> for something worth telling the user.</summary>
    protected abstract IReadOnlyList<string> Process(ConversionJob job, string input, CancellationToken ct, out string? note);

    private static void Cleanup(IEnumerable<string> outputs)
    {
        foreach (var o in outputs)
        {
            try { if (File.Exists(o)) File.Delete(o); } catch (IOException) { /* best effort */ }
        }
    }
}
