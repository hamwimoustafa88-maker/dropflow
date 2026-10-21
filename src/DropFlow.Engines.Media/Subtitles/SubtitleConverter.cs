using DropFlow.Core.Operations;
using DropFlow.Core.Output;

namespace DropFlow.Engines.Media.Subtitles;

/// <summary>
/// Subtitle conversion: <c>convert:srt</c>, <c>convert:vtt</c>, <c>convert:ass</c> and <c>convert:txt</c> from SRT, WebVTT and ASS files.
/// Pure C#, no engine. Timing is preserved to the millisecond (ASS to the centisecond, its own precision); styling that only one
/// format can express is dropped (see <see cref="SubtitleCue"/>). Output is UTF-8 (with a BOM for SRT/ASS/TXT, without for VTT).
/// No options: the converter runs immediately. Input files larger than 20 MB are refused (a subtitle file that size is not a subtitle file).
/// </summary>
public sealed class SubtitleConverter : IConverter
{
    internal const long MaxBytes = 20 * 1024 * 1024;

    private static readonly string[] Inputs = ["srt", "vtt", "ass"];
    private static readonly string[] Outputs = ["srt", "vtt", "ass", "txt"];

    private readonly OutputPathResolver _paths = new();

    public IReadOnlyCollection<string> Operations { get; } = Outputs.Select(Operation.Convert).ToArray();

    public bool Supports(string operation, string inputExtension)
    {
        if (!Operation.IsConvert(operation)) return false;
        var target = Operation.TargetOf(operation);
        var input = inputExtension.TrimStart('.').ToLowerInvariant();
        return Outputs.Contains(target) && Inputs.Contains(input) && target != input;
    }

    public EngineId EngineFor(string operation) => EngineId.None;

    public async Task<ConversionResult> RunAsync(ConversionJob job, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var input = job.Inputs[0];
        var targetExt = Operation.TargetOf(job.Operation);
        var sourceExt = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        var started = DateTime.UtcNow;

        if (!Supports(job.Operation, sourceExt))
            return ConversionResult.Fail($"Cannot convert .{sourceExt} to .{targetExt}.");
        if (!File.Exists(input))
            return ConversionResult.Fail($"Input file not found: {input}");

        string? output = null;
        var created = false; // only ever delete a file this run created
        try
        {
            if (new FileInfo(input).Length > MaxBytes)
                return ConversionResult.Fail("The file is too large to be a subtitle file.");

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);

            var from = SubtitleParser.FormatFromExtension(sourceExt)!.Value;
            var to = SubtitleParser.FormatFromExtension(targetExt)!.Value;
            var doc = SubtitleParser.ParseBytes(bytes, from);
            var text = SubtitleWriter.Write(doc, to);
            var data = SubtitleWriter.ToBytes(text, to);

            cancellationToken.ThrowIfCancellationRequested();
            output = _paths.ForFile(input, targetExt, "converted", job.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            // CreateNew: if another process created the name since it was chosen, fail instead of overwriting.
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

            progress.Report(new JobProgress(1));
            return ConversionResult.Ok([output], DateTime.UtcNow - started);
        }
        catch (SubtitleFormatException ex)
        {
            return ConversionResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (created) DeletePartial(output);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created) DeletePartial(output);
            return ConversionResult.Fail($"Could not read or write the subtitle file: {ex.Message}");
        }
    }

    private static void DeletePartial(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
