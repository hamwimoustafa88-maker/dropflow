using DropFlow.Core.Operations;
using DropFlow.Engines.Common;
using DropFlow.Engines.Media.Operations;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.Engines.Media.Runtime;

namespace DropFlow.Engines.Media;

/// <summary>
/// The runnable side of the Encoder window: probe a file, preview what <see cref="EncoderPlanner"/> would do with some
/// <see cref="EncoderSettings"/>, and run it. Not a catalog operation (<c>vid.encoder</c> opens the window; this is what the window calls).
/// Hardware encoders are used only when the settings ask for them, and only after a one-frame trial encode proves they work.
/// </summary>
public static class MediaEncoder
{
    /// <summary>Reads a file with ffprobe. Throws <see cref="MediaProbeException"/> when ffprobe is missing or the file cannot be read.</summary>
    public static async Task<MediaInfo> ProbeAsync(IEngineProvider engines, string path, CancellationToken cancellationToken = default)
    {
        var runner = new ProcessMediaToolRunner(engines);
        if (runner.FfprobePath is null) throw new MediaProbeException("ffprobe is not installed. Install FFmpeg (it includes ffprobe).");
        var result = await runner.RunFfprobeAsync(FfmpegCommand.Probe(path), new ProcessOptions { Timeout = TimeSpan.FromSeconds(60) }, cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new MediaProbeException(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        return FfprobeParser.Parse(result.StdOut, path);
    }

    /// <summary>
    /// Shows what would run (arguments, output extension, warnings, bitrates) without encoding anything. The output path inside the returned
    /// plan is <see cref="EncoderPlan.OutputPlaceholder"/>.
    /// </summary>
    public static async Task<EncoderPlan> PlanAsync(IEngineProvider engines, MediaInfo info, EncoderSettings settings, CancellationToken cancellationToken = default)
    {
        var services = new MediaServices(new ProcessMediaToolRunner(engines));
        var ctx = new MediaContext(new ConversionJob([info.Path ?? ""], "vid.encoder"), services, new Progress<JobProgress>(), cancellationToken);
        return EncoderPlanner.Plan(settings, info, await ctx.PlanContextAsync(null, settings).ConfigureAwait(false));
    }

    /// <summary>
    /// Encodes <paramref name="input"/> with <paramref name="settings"/> into <c>name (suffix).ext</c> beside it (or in <paramref name="outputDirectory"/>), never
    /// overwriting. Reports progress 0..1 and cleans up the partial file on failure or cancellation.
    /// </summary>
    public static Task<ConversionResult> RunAsync(
        IEngineProvider engines, string input, EncoderSettings settings, IProgress<JobProgress> progress,
        CancellationToken cancellationToken = default, string? outputDirectory = null, string suffix = "encoded")
    {
        var converter = new MediaOperationConverter(new EncodeSettingsOperation(suffix), new MediaServices(new ProcessMediaToolRunner(engines)));
        var job = new ConversionJob(
            [input],
            EncodeSettingsOperation.OperationId,
            new Dictionary<string, object?> { [EncodeSettingsOperation.SettingsKey] = settings },
            outputDirectory);
        return converter.RunAsync(job, progress, cancellationToken);
    }
}
