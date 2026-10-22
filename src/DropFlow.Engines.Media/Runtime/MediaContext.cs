using System.Globalization;
using DropFlow.Core.Operations;
using DropFlow.Core.Output;
using DropFlow.Engines.Common;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Runtime;

/// <summary>
/// Everything one conversion run needs: options with validation, output naming (never overwriting), probing, running FFmpeg with
/// progress, a scratch folder, and cleanup of partial outputs when the run fails or is cancelled.
/// </summary>
internal sealed class MediaContext
{
    private readonly List<string> _createdFiles = [];
    private readonly List<string> _createdFolders = [];
    private string? _tempDirectory;

    public MediaContext(ConversionJob job, MediaServices services, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        Job = job;
        Services = services;
        Progress = progress;
        Ct = cancellationToken;
    }

    public ConversionJob Job { get; }
    public MediaServices Services { get; }
    public IProgress<JobProgress> Progress { get; }
    public CancellationToken Ct { get; }
    public OutputPathResolver Paths { get; } = new();

    /// <summary>
    /// Something the user should know about a successful run (for example "nothing to trim", which writes no file). A resource key such as
    /// <c>note.media.noEdgeSilence</c> is translated by the app; see <c>DropFlow.Core.Operations.NoteText</c>.
    /// </summary>
    public string? Note { get; set; }

    public string Input => Job.Inputs[0];
    public string InputExtension => Path.GetExtension(Input).TrimStart('.').ToLowerInvariant();
    public string InputStem => Path.GetFileNameWithoutExtension(Input);

    // ------------------------------------------------------------------ options

    public double Number(string key, double fallback, double? min = null, double? max = null)
    {
        double value;
        try
        {
            value = Job.GetDouble(key, fallback);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new MediaFailure($"The option '{key}' must be a number.");
        }
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new MediaFailure($"The option '{key}' must be a number.");
        if (min is { } lo && value < lo) value = lo;
        if (max is { } hi && value > hi) value = hi;
        return value;
    }

    public int Int(string key, int fallback, int? min = null, int? max = null) =>
        (int)Math.Round(Number(key, fallback, min, max));

    public string Text(string key, string fallback = "") => Job.GetString(key, fallback)?.Trim() ?? fallback;

    public bool Flag(string key, bool fallback = false)
    {
        try
        {
            return Job.GetBool(key, fallback);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            throw new MediaFailure($"The option '{key}' must be true or false.");
        }
    }

    public string Choice(string key, string fallback, IReadOnlyCollection<string> allowed)
    {
        var value = Text(key, fallback).ToLowerInvariant();
        return allowed.Contains(value) ? value : throw new MediaFailure($"The option '{key}' must be one of: {string.Join(", ", allowed)}.");
    }

    /// <summary>Optional time option; empty means "not set". A bad value is a clear failure naming the option.</summary>
    public double? Time(string key)
    {
        var text = Text(key);
        if (text.Length == 0) return null;
        return TimeSpec.TryParseSeconds(text, out var seconds) ? seconds : throw new MediaFailure($"'{text}' is not a valid time for '{key}'. Use seconds (90.5) or hh:mm:ss.");
    }

    // ------------------------------------------------------------------ outputs

    /// <summary>Where a single-file result goes: beside the input (or the output folder), never over an existing file.</summary>
    public string NewFile(string extension, string suffix, string? basedOn = null)
    {
        var path = Paths.ForFile(basedOn ?? Input, extension, suffix, Job.OutputDirectory);
        EnsureDirectory(Path.GetDirectoryName(path));
        _createdFiles.Add(path);
        return path;
    }

    public string NewGroupFile(string extension, string suffix)
    {
        var path = Paths.ForGroup(Job.Inputs, extension, suffix, Job.OutputDirectory);
        EnsureDirectory(Path.GetDirectoryName(path));
        _createdFiles.Add(path);
        return path;
    }

    /// <summary>A new, empty folder for multi-file results; removed again if the run fails or is cancelled.</summary>
    public string NewFolder(string suffix)
    {
        var path = Paths.ForFolder(Input, suffix, Job.OutputDirectory);
        EnsureDirectory(Path.GetDirectoryName(path));
        Directory.CreateDirectory(path);
        _createdFolders.Add(path);
        return path;
    }

    private static void EnsureDirectory(string? directory)
    {
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    /// <summary>A scratch folder with a plain ASCII path, deleted when the run ends.</summary>
    public string TempDirectory
    {
        get
        {
            if (_tempDirectory is not null) return _tempDirectory;
            _tempDirectory = Path.Combine(Path.GetTempPath(), "dropflow-media-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(_tempDirectory);
            return _tempDirectory;
        }
    }

    /// <summary>Throws unless <paramref name="path"/> exists and is not empty.</summary>
    public static void RequireOutput(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new MediaFailure("FFmpeg finished but produced no output.");
    }

    public void CleanupOutputs()
    {
        foreach (var f in _createdFiles) TryDelete(() => { if (File.Exists(f)) File.Delete(f); });
        foreach (var d in _createdFolders) TryDelete(() => { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); });
    }

    public void CleanupTemp()
    {
        if (_tempDirectory is { } dir) TryDelete(() => { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); });
    }

    private static void TryDelete(Action action)
    {
        try { action(); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // ------------------------------------------------------------------ probing

    public bool HasFfprobe => Services.Runner.FfprobePath is not null;

    /// <summary>
    /// ffprobe result, or null when ffprobe is not installed (the caller then degrades: no progress fraction, no validation).
    /// A file ffprobe cannot read is a <see cref="MediaFailure"/>: it is corrupt or not media.
    /// </summary>
    public async Task<MediaInfo?> TryProbeAsync(string path)
    {
        if (!HasFfprobe) return null;
        var result = await Services.Runner.RunFfprobeAsync(FfmpegCommand.Probe(path), new ProcessOptions { Timeout = TimeSpan.FromSeconds(60) }, Ct).ConfigureAwait(false);
        Ct.ThrowIfCancellationRequested();
        if (!result.Success)
            throw new MediaFailure($"\"{Path.GetFileName(path)}\" could not be read: " + FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        try
        {
            return FfprobeParser.Parse(result.StdOut, path);
        }
        catch (MediaProbeException ex)
        {
            throw new MediaFailure($"\"{Path.GetFileName(path)}\" could not be read: {ex.Message}");
        }
    }

    /// <summary>Like <see cref="TryProbeAsync"/> but for operations that cannot work without the numbers (size, channels, streams).</summary>
    public async Task<MediaInfo> ProbeAsync(string path) =>
        await TryProbeAsync(path).ConfigureAwait(false)
        ?? throw new MediaFailure("ffprobe was not found next to ffmpeg. Reinstall FFmpeg (it ships both programs); this tool needs ffprobe to read the file.");

    public async Task<FfmpegEncoders> EncodersAsync() =>
        await Services.GetEncodersAsync(Ct).ConfigureAwait(false) ?? FfmpegEncoders.AssumeSoftwareDefaults;

    // ------------------------------------------------------------------ running

    /// <summary>
    /// Runs ffmpeg and reports progress mapped into <paramref name="from"/>..<paramref name="to"/> (0..1) using
    /// <paramref name="expectedOutputSeconds"/> as the denominator. Cancellation kills the process and throws.
    /// </summary>
    public async Task<ProcessResult> RunFfmpegAsync(IReadOnlyList<string> args, double? expectedOutputSeconds = null, double from = 0, double to = 1, string? workingDirectory = null, string? label = null)
    {
        var parser = new FfmpegProgressParser();
        var options = new ProcessOptions
        {
            WorkingDirectory = workingDirectory,
            OnOutputLine = line =>
            {
                if (parser.Feed(line) is not { } p) return;
                var fraction = from + (to - from) * p.Fraction(expectedOutputSeconds);
                var message = p.Speed is { } speed ? $"{label ?? "Encoding"} ({speed:0.#}x)" : label;
                Progress.Report(new JobProgress(Math.Clamp(fraction, 0, 1), message));
            },
        };
        var result = await Services.Runner.RunFfmpegAsync(args, options, Ct).ConfigureAwait(false);
        if (result.Cancelled || Ct.IsCancellationRequested) throw new OperationCanceledException(Ct);
        return result;
    }

    /// <summary>Runs ffmpeg and turns a non-zero exit into a <see cref="MediaFailure"/> with a readable reason.</summary>
    public async Task RunFfmpegOrFailAsync(IReadOnlyList<string> args, double? expectedOutputSeconds = null, double from = 0, double to = 1, string? workingDirectory = null, string? label = null)
    {
        var result = await RunFfmpegAsync(args, expectedOutputSeconds, from, to, workingDirectory, label).ConfigureAwait(false);
        if (!result.Success) throw new MediaFailure(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        Progress.Report(new JobProgress(to));
    }

    /// <summary>
    /// Builds the planner context for an output path with the encoders of the installed build. Hardware encoders are only looked at
    /// when <paramref name="settings"/> asks for them; each candidate is vetted with a one-frame trial encode (cached), so a missing GPU
    /// or a broken driver falls back to software instead of failing the job.
    /// </summary>
    public async Task<PlanContext> PlanContextAsync(string? outputPath, EncoderSettings? settings = null)
    {
        var encoders = await EncodersAsync().ConfigureAwait(false);
        if (settings is null || settings.Hardware == HardwarePreference.None || settings.VideoCodec is VideoCodec.Copy or VideoCodec.Vp9)
            return new PlanContext { OutputPath = outputPath, Encoders = encoders };

        var capabilities = HardwareEncoders.Detect(encoders);
        var works = new Dictionary<string, bool>();
        foreach (var candidate in capabilities.Encoders.Where(e => e.Codec == settings.VideoCodec))
            works[candidate.Name] = await Services.IsHardwareEncoderUsableAsync(candidate.Name, Ct).ConfigureAwait(false);
        return new PlanContext
        {
            OutputPath = outputPath,
            Encoders = encoders,
            Hardware = capabilities,
            HardwareWorks = name => works.GetValueOrDefault(name),
        };
    }

    /// <summary>
    /// Plans and runs an encode. Two-pass plans run in the scratch folder so the pass statistics land there. Warnings the planner
    /// produced are shown as the progress message.
    /// <list type="bullet">
    /// <item><b>Hardware fallback:</b> if a hardware encoder was planned and FFmpeg fails anyway (driver, options this FFmpeg version does not know),
    ///   the partial output is removed and the job is planned again with software encoding, so choosing "Auto" can never make a job fail.</item>
    /// <item><b>Target size:</b> encoders are only approximately accurate, so the result is measured and, when it is above the target or more than
    ///   5% below it, the last pass is repeated with a corrected bitrate (at most twice; see <see cref="RefineTargetSizeAsync"/>).</item>
    /// </list>
    /// </summary>
    public async Task EncodeAsync(EncoderSettings settings, MediaInfo info, string outputPath, double from = 0, double to = 1, double? expectedSecondsOverride = null)
    {
        var plan = EncoderPlanner.Plan(settings, info, await PlanContextAsync(outputPath, settings).ConfigureAwait(false));
        if (!plan.Ok) throw new MediaFailure(plan.Error!);

        var refine = settings.RateControl == RateControl.TargetSize && settings.TargetSizeMb is > 0;
        var mainEnd = refine ? from + (to - from) * 0.7 : to;
        try
        {
            await RunPlanAsync(plan, outputPath, from, mainEnd, expectedSecondsOverride).ConfigureAwait(false);
        }
        catch (MediaFailure failure) when (UsesHardware(plan))
        {
            Progress.Report(new JobProgress(from, $"The {plan.VideoEncoder} hardware encoder failed ({failure.Message}); encoding in software instead."));
            if (File.Exists(outputPath)) File.Delete(outputPath);
            plan = EncoderPlanner.Plan(settings with { Hardware = HardwarePreference.None }, info, new PlanContext { OutputPath = outputPath, Encoders = await EncodersAsync().ConfigureAwait(false) });
            if (!plan.Ok) throw new MediaFailure(plan.Error!);
            await RunPlanAsync(plan, outputPath, from, mainEnd, expectedSecondsOverride).ConfigureAwait(false);
        }

        if (refine) await RefineTargetSizeAsync(plan, settings.TargetSizeMb!.Value, outputPath, mainEnd, to, expectedSecondsOverride).ConfigureAwait(false);
    }

    private static bool UsesHardware(EncoderPlan plan) =>
        plan.VideoEncoder is { } e && (e.EndsWith("_nvenc", StringComparison.Ordinal) || e.EndsWith("_qsv", StringComparison.Ordinal) || e.EndsWith("_amf", StringComparison.Ordinal));

    /// <summary>The band a target-size result should land in, as a fraction of the target. Above 1.0 would break an upload limit; below 0.95 wastes the budget.</summary>
    internal const double TargetBandLow = 0.95;
    internal const double TargetBandHigh = 1.0;
    private const double TargetAim = 0.975;
    /// <summary>A file over the target is re-encoded with its bitrate scaled by (target / actual) times this margin.</summary>
    internal const double OvershootMargin = 0.95;
    internal const int MaxSizeCorrections = 3;

    /// <summary>
    /// Measures the finished file and, if it is outside <see cref="TargetBandLow"/>..<see cref="TargetBandHigh"/> of the target, re-runs the final
    /// pass with a corrected video bitrate (two-pass statistics from pass 1 are reused, so a correction costs one pass), at most
    /// <see cref="MaxSizeCorrections"/> times:
    /// <list type="bullet">
    /// <item><b>Too big</b> (the size is a promise: an upload limit): the bitrate is scaled by (target / actual) × <see cref="OvershootMargin"/>, and keeps
    ///   going down on every attempt; a smaller file replaces the bigger one. If the file is <b>still</b> over the target after the last attempt the job
    ///   fails with a clear message (and the file is removed) rather than delivering something that will not fit.</item>
    /// <item><b>Far under</b> (below 95 %): the bitrate is raised by the miss (at most +50 % per step); the new file is kept only when it is closer to
    ///   the aim. A failed correction keeps the smaller first result, which is still within the limit.</item>
    /// </list>
    /// </summary>
    private async Task RefineTargetSizeAsync(EncoderPlan plan, double targetMb, string outputPath, double from, double to, double? expectedSecondsOverride)
    {
        if (plan.VideoBitrateKbps is not > 0 || plan.OutputDurationSeconds is not > 0 || plan.Passes.Count == 0) return;

        var targetBytes = targetMb * 1_000_000;
        var duration = plan.OutputDurationSeconds.Value;
        var seconds = expectedSecondsOverride ?? duration;
        var work = plan.IsTwoPass ? TempDirectory : null;
        var planned = (double)plan.VideoBitrateKbps.Value;
        var bitrate = planned;
        var template = plan.Passes[^1];
        var attempts = 0;

        for (var attempt = 0; attempt < MaxSizeCorrections; attempt++)
        {
            var actual = new FileInfo(outputPath).Length;
            var ratio = actual / targetBytes;
            if (ratio is >= TargetBandLow and <= TargetBandHigh) break;

            var over = ratio > TargetBandHigh;
            double corrected;
            if (over)
            {
                corrected = bitrate * (targetBytes / actual) * OvershootMargin;
            }
            else
            {
                var nominalBytes = bitrate * 1000 * duration / 8;
                var wantedKbps = (nominalBytes - (actual - targetBytes * TargetAim)) * 8 / duration / 1000;
                corrected = Math.Clamp(wantedKbps, bitrate * 0.5, bitrate * 1.5);
            }
            if (corrected < BitrateMath.MinVideoKbps) break;

            var candidate = Path.Combine(TempDirectory, $"refine{attempt}{Path.GetExtension(outputPath)}");
            var args = ScaleBitrates(template, corrected / planned, outputPath, candidate);
            var a = from + (to - from) * attempt / MaxSizeCorrections;
            var b = from + (to - from) * (attempt + 1) / MaxSizeCorrections;
            Progress.Report(new JobProgress(a, $"The file is {actual / 1_000_000.0:0.##} MB, aiming for {targetMb:0.##} MB; adjusting."));

            attempts++;
            var result = await RunFfmpegAsync(args, seconds, a, b, work, "Adjusting size").ConfigureAwait(false);
            if (!result.Success || !File.Exists(candidate) || new FileInfo(candidate).Length == 0) break; // keep what we have

            var newSize = new FileInfo(candidate).Length;
            var better = over
                ? newSize < actual
                : Math.Abs(newSize - targetBytes * TargetAim) < Math.Abs(actual - targetBytes * TargetAim) && newSize <= targetBytes;
            if (better) File.Move(candidate, outputPath, overwrite: true);
            else File.Delete(candidate);

            if (over) bitrate = corrected;          // still too big or not: the next attempt starts lower
            else if (better) bitrate = corrected;
            else break;
        }

        var finalSize = new FileInfo(outputPath).Length;
        if (finalSize > targetBytes)
            throw new MediaFailure(
                $"The result is {finalSize / 1_000_000.0:0.##} MB, over the {targetMb:0.##} MB target even after {attempts} " +
                $"{(attempts == 1 ? "attempt" : "attempts")} with a lower bitrate. Choose a larger size, a lower audio bitrate, or trim the video.");
        Progress.Report(new JobProgress(to));
    }

    /// <summary>Copy of <paramref name="args"/> with the video rate options scaled by <paramref name="factor"/> and the output path replaced.</summary>
    internal static IReadOnlyList<string> ScaleBitrates(IReadOnlyList<string> args, double factor, string oldOutput, string newOutput)
    {
        var result = new List<string>(args);
        for (var i = 0; i + 1 < result.Count; i++)
        {
            if (result[i] is "-b:v" or "-maxrate" or "-bufsize" && result[i + 1].EndsWith('k')
                && double.TryParse(result[i + 1].TrimEnd('k'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                result[i + 1] = $"{Math.Max(1, (int)Math.Round(value * factor))}k";
        }
        if (result.Count > 0 && result[^1] == oldOutput) result[^1] = newOutput;
        return result;
    }

    public async Task RunPlanAsync(EncoderPlan plan, string outputPath, double from = 0, double to = 1, double? expectedSecondsOverride = null)
    {
        if (plan.Warnings.Count > 0) Progress.Report(new JobProgress(from, string.Join(" ", plan.Warnings)));
        var seconds = expectedSecondsOverride ?? plan.OutputDurationSeconds;
        var work = plan.IsTwoPass ? TempDirectory : null;
        for (var i = 0; i < plan.Passes.Count; i++)
        {
            var a = from + (to - from) * i / plan.Passes.Count;
            var b = from + (to - from) * (i + 1) / plan.Passes.Count;
            var label = plan.IsTwoPass ? $"Pass {i + 1} of 2" : null;
            await RunFfmpegOrFailAsync(plan.Passes[i], seconds, a, b, work, label).ConfigureAwait(false);
        }
        RequireOutput(outputPath);
    }

    // ------------------------------------------------------------------ small helpers shared by operations

    /// <summary>The input's extension when it is a modern container, otherwise MP4 (re-encoding into AVI/WMV/FLV/M4V is not offered by the tools).</summary>
    public static string ReencodeExtension(string inputExtension) =>
        inputExtension is "mp4" or "mov" or "mkv" or "webm" ? inputExtension : "mp4";

    public static ContainerFormat ContainerOf(string extension) => extension switch
    {
        "mkv" => ContainerFormat.Mkv,
        "webm" => ContainerFormat.Webm,
        "mov" => ContainerFormat.Mov,
        "gif" => ContainerFormat.Gif,
        _ => ContainerFormat.Mp4,
    };

    public static string Inv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
