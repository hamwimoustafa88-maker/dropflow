using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.Engines.Media.Runtime;

namespace DropFlow.Engines.Media.Operations;

/// <summary>Common shape of the single-input audio tools.</summary>
internal abstract class AudioToolBase : MediaOperation
{
    protected abstract string Id { get; }

    public override IReadOnlyCollection<string> Ids => [Id];

    public override bool Supports(string operation, string inputExtension) => operation == Id && AudioFormats.IsAudioExtension(inputExtension);

    /// <summary>
    /// Runs an audio re-encode, carrying cover art over when the formats allow it. A muxer that rejects the picture (it happens for
    /// some pairs) triggers one retry without it, because the sound matters more than the artwork.
    /// </summary>
    protected static async Task EncodeAudioAsync(
        MediaContext ctx, string output, string outputExtension, MediaInfo? info, int? bitrateKbps, AudioQuality quality,
        string? audioFilter, double? expectedSeconds, double from = 0, double to = 1, string? label = null, int? sampleRate = null)
    {
        var encoders = await ctx.EncodersAsync().ConfigureAwait(false);
        var command = ConversionArgs.Audio(ctx.Input, output, outputExtension, info, encoders, bitrateKbps, includeCoverArt: true, quality, audioFilter, sampleRate);
        if (!command.Ok) throw new MediaFailure(command.Error!);
        if (command.Warnings.Count > 0) ctx.Progress.Report(new JobProgress(from, string.Join(" ", command.Warnings)));

        var result = await ctx.RunFfmpegAsync(command.Args, expectedSeconds, from, to, label: label).ConfigureAwait(false);
        if (!result.Success && command.Args.Contains("attached_pic"))
        {
            if (File.Exists(output)) File.Delete(output);
            command = ConversionArgs.Audio(ctx.Input, output, outputExtension, info, encoders, bitrateKbps, includeCoverArt: false, quality, audioFilter, sampleRate);
            result = await ctx.RunFfmpegAsync(command.Args, expectedSeconds, from, to, label: label).ConfigureAwait(false);
        }
        if (!result.Success) throw new MediaFailure(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        MediaContext.RequireOutput(output);
    }
}

/// <summary>
/// <b>aud.compress</b> — smaller audio. Options: <c>bitrate</c> (64 | 96 | 128 | 160 | 192 | 256 kbps, default 128), <c>format</c> (same | mp3 | m4a | ogg | opus, default same).
/// "same" keeps lossy formats as they are; lossless inputs (WAV, FLAC, AIFF) become M4A because lossless data cannot shrink without going lossy.
/// Tags and cover art are kept. Output: <c>name (compressed).ext</c>.
/// </summary>
internal sealed class AudioCompressOperation : AudioToolBase
{
    protected override string Id => "aud.compress";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("bitrate", ToolOptionKind.Choice, "128", Choices: ["64", "96", "128", "160", "192", "256"]),
        new("format", ToolOptionKind.Choice, "same", Choices: ["same", "mp3", "m4a", "ogg", "opus"]),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var bitrate = int.Parse(ctx.Choice("bitrate", "128", ["64", "96", "128", "160", "192", "256"]), System.Globalization.CultureInfo.InvariantCulture);
        var format = ctx.Choice("format", "same", ["same", "mp3", "m4a", "ogg", "opus"]);
        var ext = format != "same" ? format : AudioFormats.IsLossless(ctx.InputExtension) ? "m4a" : ctx.InputExtension;

        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        if (info is not null && !info.HasAudio) throw new MediaFailure("The file has no audio stream.");
        var output = ctx.NewFile(ext, "compressed");
        await EncodeAudioAsync(ctx, output, ext, info, bitrate, AudioQuality.Standard, null, info?.DurationSeconds).ConfigureAwait(false);
        return [output];
    }
}

/// <summary>
/// <b>aud.metadata</b> — edit tags without re-encoding. Options: <c>title</c>, <c>artist</c>, <c>album</c>, <c>comment</c> (empty = leave as is),
/// <c>strip</c> (true removes every tag and the cover picture). Output: <c>name (edited).ext</c>.
/// </summary>
internal sealed class AudioMetadataOperation : AudioToolBase
{
    protected override string Id => "aud.metadata";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("title", ToolOptionKind.Text, ""),
        new("artist", ToolOptionKind.Text, ""),
        new("album", ToolOptionKind.Text, ""),
        new("comment", ToolOptionKind.Text, ""),
        new("strip", ToolOptionKind.Bool, false),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var strip = ctx.Flag("strip");
        var title = ctx.Text("title");
        var artist = ctx.Text("artist");
        var album = ctx.Text("album");
        var comment = ctx.Text("comment");
        if (!strip && title.Length + artist.Length + album.Length + comment.Length == 0)
            throw new MediaFailure("Enter at least one tag value, or choose to remove all metadata.");

        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        var output = ctx.NewFile(ctx.InputExtension, "edited");
        var args = AudioToolArgs.Metadata(ctx.Input, output, ctx.InputExtension, info, title, artist, album, comment, strip);
        var result = await ctx.RunFfmpegAsync(args, info?.DurationSeconds).ConfigureAwait(false);
        if (!result.Success && !strip && info?.CoverArt is not null && AudioFormats.CarriesCoverArt(ctx.InputExtension))
        {
            // Retry without the cover picture: some muxers refuse to copy it unchanged.
            if (File.Exists(output)) File.Delete(output);
            result = await ctx.RunFfmpegAsync(AudioToolArgs.Metadata(ctx.Input, output, ctx.InputExtension, null, title, artist, album, comment, strip), info.DurationSeconds).ConfigureAwait(false);
        }
        if (!result.Success) throw new MediaFailure(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        MediaContext.RequireOutput(output);
        return [output];
    }
}

/// <summary>
/// <b>aud.normalize</b> — even out the volume to the EBU R128 loudness standard, in two passes: pass 1 measures (loudnorm prints its JSON measurements to stderr),
/// pass 2 applies exactly those measured values as a linear gain and writes the file at the source sample rate (loudnorm itself outputs 192 kHz, so <c>-ar</c> is passed).
/// Options: <c>target</c> (integrated loudness in LUFS, -40..-5, default -16), <c>truePeak</c> (dBTP, -9..0, default -1.5), <c>lra</c> (loudness range, 1-50, default 11).
/// A silent file fails with a message. Re-encodes at higher quality settings to avoid generation loss. Output: <c>name (normalized).ext</c>.
/// </summary>
internal sealed class AudioNormalizeOperation : AudioToolBase
{
    protected override string Id => "aud.normalize";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("target", ToolOptionKind.Number, -16, Min: -40, Max: -5),
        new("truePeak", ToolOptionKind.Number, -1.5, Min: -9, Max: 0),
        new("lra", ToolOptionKind.Number, 11, Min: 1, Max: 50),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var target = ctx.Number("target", -16, -40, -5);
        var truePeak = ctx.Number("truePeak", -1.5, -9, 0);
        var lra = ctx.Number("lra", 11, 1, 50);

        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        if (info is not null && !info.HasAudio) throw new MediaFailure("The file has no audio stream.");
        var duration = info?.DurationSeconds;
        var sampleRate = info?.Audio?.SampleRate is > 0 ? info.Audio.SampleRate!.Value : 44100;

        var measure = await ctx.RunFfmpegAsync(AudioToolArgs.Analyze(ctx.Input, Loudnorm.Pass1Filter(target, truePeak, lra)), duration, 0, 0.45, label: "Measuring loudness").ConfigureAwait(false);
        if (!measure.Success) throw new MediaFailure(FfmpegErrors.Summarize(measure.StdErr, measure.ExitCode));

        var stats = Loudnorm.ParseStats(measure.StdErr) ?? throw new MediaFailure("The loudness could not be measured (FFmpeg printed no measurement).");
        if (!stats.IsUsable) throw new MediaFailure("The audio is silent or too quiet to measure, so it cannot be normalized.");

        var output = ctx.NewFile(ctx.InputExtension, "normalized");
        await EncodeAudioAsync(ctx, output, ctx.InputExtension, info, null, AudioQuality.High, Loudnorm.Pass2Filter(stats, target, truePeak, lra), duration, 0.5, 1, "Normalizing", sampleRate).ConfigureAwait(false);
        return [output];
    }
}

/// <summary>
/// <b>aud.trim</b> — keep a section. Options: <c>start</c> and <c>end</c> (seconds or mm:ss, empty end = to the end), <c>autoSilence</c> (true cuts silence at the start
/// and the end only; silence in the middle is never touched), <c>silenceDb</c> (threshold, -90..-10 dB, default -50), <c>minSilence</c> (shortest silence that counts, 0.05-10 s, default 0.5).
/// With autoSilence the detected sound range is intersected with start/end, and 0.1 s of silence is kept next to the sound. Stream copy: no quality loss, accurate to one audio frame.
/// Output: <c>name (trimmed).ext</c>.
/// </summary>
internal sealed class AudioTrimOperation : AudioToolBase
{
    protected override string Id => "aud.trim";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("start", ToolOptionKind.Text, "0"),
        new("end", ToolOptionKind.Text, ""),
        new("autoSilence", ToolOptionKind.Bool, false),
        new("silenceDb", ToolOptionKind.Number, -50, Min: -90, Max: -10),
        new("minSilence", ToolOptionKind.Number, 0.5, Min: 0.05, Max: 10),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var start = ctx.Time("start") ?? 0;
        var end = ctx.Time("end");
        var auto = ctx.Flag("autoSilence");
        if (end is { } e0 && e0 <= start) throw new MediaFailure("The end time must be after the start time.");
        if (!auto && start <= 0 && end is null) throw new MediaFailure("Set a start or end time, or turn on automatic silence trimming.");

        var info = auto ? await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false) : await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        if (info is not null && !info.HasAudio) throw new MediaFailure("The file has no audio stream.");
        var duration = info?.DurationSeconds;

        if (auto)
        {
            if (duration is not > 0) throw new MediaFailure("The length of the file is unknown, so silence at its end cannot be found.");
            var filter = SilenceDetect.Filter(ctx.Number("silenceDb", -50, -90, -10), ctx.Number("minSilence", 0.5, 0.05, 10));
            var scan = await ctx.RunFfmpegAsync(AudioToolArgs.Analyze(ctx.Input, filter), duration, 0, 0.5, label: "Looking for silence").ConfigureAwait(false);
            if (!scan.Success) throw new MediaFailure(FfmpegErrors.Summarize(scan.StdErr, scan.ExitCode));

            var range = SilenceDetect.EdgeTrim(SilenceDetect.Parse(scan.StdErr), duration.Value)
                        ?? throw new MediaFailure("The whole file is silent at this threshold, so there is nothing to keep.");
            start = Math.Max(start, range.Start);
            var autoEnd = range.End >= duration.Value - 0.01 ? (double?)null : range.End;
            end = (end, autoEnd) switch
            {
                (null, var a) => a,
                (var m, null) => m,
                (var m, var a) => Math.Min(m!.Value, a!.Value),
            };
        }

        if (duration is > 0)
        {
            if (start >= duration) throw new MediaFailure($"The start time is past the end of the file ({TimeSpec.FormatClock(duration.Value)} long).");
            if (end > duration) end = duration;
        }
        if (end is { } e && e <= start) throw new MediaFailure("Nothing is left between the start and end times.");

        var output = ctx.NewFile(ctx.InputExtension, "trimmed");
        var args = AudioToolArgs.TrimCopy(ctx.Input, output, ctx.InputExtension, info, start, end);
        await ctx.RunFfmpegOrFailAsync(args, (end ?? duration) - start, auto ? 0.5 : 0, 1).ConfigureAwait(false);
        MediaContext.RequireOutput(output);
        return [output];
    }
}

/// <summary>
/// <b>aud.trim-silence</b> — removes silence at the start and/or the end of a recording (never in the middle). Works on audio files and, because the
/// catalog also lists it for videos, on the sound of a video. Options: <c>silenceDb</c> (what counts as silence, -90..-10 dB, default -45),
/// <c>ends</c> (both | start | end, default both). 0.1 s of silence is kept next to the sound.
/// <list type="bullet">
/// <item><b>Audio</b>: <c>silenceremove</c> on the start, and on the end by reversing (see <see cref="SilenceTrim"/>); sample-accurate, re-encoded at high
///   quality in the same format. Files longer than 10 minutes are measured first and cut with <c>atrim</c> to keep memory flat.</item>
/// <item><b>Video</b>: the sound is measured with <c>silencedetect</c> and the whole file is cut to that range by <b>stream copy</b> (picture and sound
///   stay in sync and nothing is re-encoded). Like <c>vid.trim</c> without re-encoding, the cut starts on a keyframe, so up to one keyframe
///   interval of the leading silence can remain.</item>
/// </list>
/// When there is no silence at the chosen ends, nothing is written and the note <c>note.media.noEdgeSilence</c> says so. Output: <c>name (trimmed).ext</c>.
/// </summary>
internal sealed class AudioTrimSilenceOperation : AudioToolBase
{
    public static readonly IReadOnlyList<string> EndChoices = ["both", "start", "end"];

    protected override string Id => "aud.trim-silence";

    public override bool Supports(string operation, string inputExtension) =>
        operation == Id && (AudioFormats.IsAudioExtension(inputExtension) || VideoToolBase.Inputs.Contains(inputExtension));

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("silenceDb", ToolOptionKind.Number, -45, Min: -90, Max: -10),
        new("ends", ToolOptionKind.Choice, "both", Choices: EndChoices),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var threshold = ctx.Number("silenceDb", -45, -90, -10);
        var ends = ctx.Choice("ends", "both", EndChoices) switch { "start" => SilenceEnds.Start, "end" => SilenceEnds.End, _ => SilenceEnds.Both };
        var isVideo = VideoToolBase.Inputs.Contains(ctx.InputExtension);

        var info = isVideo ? await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false) : await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        if (info is not null && !info.HasAudio) throw new MediaFailure("The file has no audio stream.");
        var duration = info?.DurationSeconds;

        if (!isVideo && SilenceTrim.UsesReversal(duration))
        {
            var output = ctx.NewFile(ctx.InputExtension, "trimmed");
            await EncodeAudioAsync(ctx, output, ctx.InputExtension, info, null, AudioQuality.High, SilenceTrim.RemoveFilter(threshold, ends), duration, label: "Trimming silence").ConfigureAwait(false);
            return [output];
        }

        // Measure, then cut: videos (so the picture stays in sync) and long audio (so nothing is held in memory).
        if (duration is not > 0) throw new MediaFailure("The length of the file is unknown, so silence at its end cannot be found.");
        var scan = await ctx.RunFfmpegAsync(AudioToolArgs.Analyze(ctx.Input, SilenceDetect.Filter(threshold, SilenceTrim.KeepSeconds)), duration, 0, 0.5, label: "Looking for silence").ConfigureAwait(false);
        if (!scan.Success) throw new MediaFailure(FfmpegErrors.Summarize(scan.StdErr, scan.ExitCode));

        var sound = SilenceDetect.EdgeTrim(SilenceDetect.Parse(scan.StdErr), duration.Value, SilenceTrim.KeepSeconds)
                    ?? throw new MediaFailure("The whole file is silent at this threshold, so there is nothing to keep.");
        if (SilenceTrim.KeepRange(sound, duration.Value, ends) is not { } keep)
        {
            ctx.Note = "note.media.noEdgeSilence";
            return [];
        }

        var target = ctx.NewFile(ctx.InputExtension, "trimmed");
        var length = (keep.End ?? duration.Value) - keep.Start;
        if (isVideo)
        {
            await ctx.RunFfmpegOrFailAsync(VideoToolArgs.TrimCopy(ctx.Input, target, ctx.InputExtension, keep.Start, keep.End), length, 0.5, 1, label: "Cutting").ConfigureAwait(false);
            MediaContext.RequireOutput(target);
        }
        else
        {
            await EncodeAudioAsync(ctx, target, ctx.InputExtension, info, null, AudioQuality.High, SilenceTrim.AtrimFilter(keep.Start, keep.End), length, 0.5, 1, "Trimming silence").ConfigureAwait(false);
        }
        return [target];
    }
}

/// <summary>
/// <b>aud.channels</b> — convert between mono and stereo. Option: <c>channels</c> (mono | stereo, default mono). Mono to stereo duplicates the signal at full level;
/// stereo to mono averages left and right; surround files are down-mixed. A file that already has the wanted layout fails with a message.
/// Output: <c>name (mono).ext</c> or <c>name (stereo).ext</c>.
/// </summary>
internal sealed class AudioChannelsOperation : AudioToolBase
{
    protected override string Id => "aud.channels";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("channels", ToolOptionKind.Choice, "mono", Choices: ["mono", "stereo"]),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var target = ctx.Choice("channels", "mono", ["mono", "stereo"]);
        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var source = info.Audio?.Channels ?? throw new MediaFailure("The file has no audio stream.");

        var filter = AudioToolArgs.ChannelsFilter(target, source);
        if (filter is null) throw new MediaFailure($"The file is already {target}.");

        var output = ctx.NewFile(ctx.InputExtension, target);
        await EncodeAudioAsync(ctx, output, ctx.InputExtension, info, null, AudioQuality.High, filter, info.DurationSeconds).ConfigureAwait(false);
        return [output];
    }
}

/// <summary>
/// <b>aud.visualize</b> — a waveform video (MP4, H.264 + AAC) from audio. Options: <c>layout</c> (landscape 1920x1080 | portrait 1080x1920 | square 1080x1080, default landscape),
/// <c>color</c> (waveform colour: name or #RRGGBB, default orange), <c>background</c> (path of a picture to show behind the wave; empty = black).
/// Output: <c>name (visualized).mp4</c>.
/// </summary>
internal sealed class AudioVisualizeOperation : AudioToolBase
{
    private static readonly string[] ImageExtensions = ["png", "jpg", "jpeg", "bmp", "webp", "tif", "tiff", "gif"];

    protected override string Id => "aud.visualize";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("layout", ToolOptionKind.Choice, "landscape", Choices: ["landscape", "portrait", "square"]),
        new("color", ToolOptionKind.Text, "orange"),
        new("background", ToolOptionKind.Text, ""),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var layout = ctx.Choice("layout", "landscape", ["landscape", "portrait", "square"]);
        var color = ctx.Text("color", "orange");
        if (!ToolFilters.IsValidColor(color)) throw new MediaFailure($"'{color}' is not a colour. Use a name such as orange or a code such as #FF6A00.");
        if (color.StartsWith('#')) color = "0x" + color[1..];

        var background = ctx.Text("background");
        if (background.Length > 0)
        {
            if (!File.Exists(background)) throw new MediaFailure($"The background picture was not found: {background}");
            if (!ImageExtensions.Contains(Path.GetExtension(background).TrimStart('.').ToLowerInvariant()))
                throw new MediaFailure("The background must be a picture (png, jpg, bmp, webp, tiff or gif).");
        }

        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        if (info is not null && !info.HasAudio) throw new MediaFailure("The file has no audio stream.");

        var output = ctx.NewFile("mp4", "visualized");
        var command = AudioToolArgs.Visualize(ctx.Input, background.Length > 0 ? background : null, output, layout, color, await ctx.EncodersAsync().ConfigureAwait(false));
        if (!command.Ok) throw new MediaFailure(command.Error!);
        await ctx.RunFfmpegOrFailAsync(command.Args, info?.DurationSeconds, label: "Rendering").ConfigureAwait(false);
        MediaContext.RequireOutput(output);
        return [output];
    }
}

/// <summary>
/// <b>aud.bleep</b> — replace parts with a beep. Options: <c>ranges</c> (required; "1.5-2.3; 10-11", "0:10-0:12", or JSON [[1.5,2.3],[10,11]]; seconds or mm:ss, decimals with a dot),
/// <c>frequency</c> (Hz, 200-4000, default 1000), <c>volume</c> (0.05-1, default 0.35). Sample-accurate: one expression per channel plays the original outside the ranges and a sine tone
/// inside. Overlapping ranges are merged. Surround files become stereo. Output: <c>name (bleeped).ext</c>.
/// </summary>
internal sealed class AudioBleepOperation : AudioToolBase
{
    protected override string Id => "aud.bleep";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("ranges", ToolOptionKind.Text, "", Required: true),
        new("frequency", ToolOptionKind.Number, 1000, Min: 200, Max: 4000),
        new("volume", ToolOptionKind.Number, 0.35, Min: 0.05, Max: 1),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        IReadOnlyList<TimeRange> ranges;
        try
        {
            ranges = TimeSpec.ParseRanges(ctx.Text("ranges"));
        }
        catch (FormatException ex)
        {
            throw new MediaFailure(ex.Message);
        }
        if (ranges.Count == 0) throw new MediaFailure("Enter at least one time range to bleep, for example 1.5-2.3.");

        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var audio = info.Audio ?? throw new MediaFailure("The file has no audio stream.");
        if (info.DurationSeconds is > 0 && ranges.All(r => r.Start >= info.DurationSeconds))
            throw new MediaFailure($"All ranges are past the end of the file ({TimeSpec.FormatClock(info.DurationSeconds.Value)} long).");

        var channels = audio.Channels is > 0 ? audio.Channels.Value : 2;
        var bleep = ToolFilters.Bleep(ranges, Math.Min(channels, 2), ctx.Number("frequency", 1000, 200, 4000), ctx.Number("volume", 0.35, 0.05, 1));
        var filter = channels > 2 ? "aformat=channel_layouts=stereo," + bleep : bleep;

        var output = ctx.NewFile(ctx.InputExtension, "bleeped");
        await EncodeAudioAsync(ctx, output, ctx.InputExtension, info, null, AudioQuality.High, filter, info.DurationSeconds).ConfigureAwait(false);
        return [output];
    }
}

/// <summary>
/// <b>aud.merge</b> (multi-file) — one audio file from several, in the order given. Option: <c>format</c> (auto | mp3 | m4a | wav | flac | ogg | opus, default auto: the inputs' common
/// format, or M4A when they differ). When every input has the same codec, sample rate, channels and container the streams are copied (instant, lossless); otherwise each input is
/// resampled to the first one's rate and brought to one channel layout, then joined with the concat filter and re-encoded. Output: <c>first name (merged).ext</c>.
/// </summary>
internal sealed class AudioMergeOperation : AudioToolBase
{
    protected override string Id => "aud.merge";

    public override bool IsMulti => true;

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("format", ToolOptionKind.Choice, "auto", Choices: ["auto", "mp3", "m4a", "wav", "flac", "ogg", "opus"]),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var inputs = ctx.Job.Inputs;
        var infos = new List<MediaInfo>();
        foreach (var input in inputs)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            if (!AudioFormats.IsAudioExtension(Path.GetExtension(input).TrimStart('.').ToLowerInvariant()))
                throw new MediaFailure($"\"{Path.GetFileName(input)}\" is not an audio file.");
            var info = await ctx.ProbeAsync(input).ConfigureAwait(false);
            if (!info.HasAudio) throw new MediaFailure($"\"{Path.GetFileName(input)}\" has no audio stream.");
            infos.Add(info);
        }

        var extensions = inputs.Select(i => Path.GetExtension(i).TrimStart('.').ToLowerInvariant()).ToList();
        var format = ctx.Choice("format", "auto", ["auto", "mp3", "m4a", "wav", "flac", "ogg", "opus"]);
        var ext = AudioToolArgs.MergeExtension(extensions, format);
        var total = infos.Sum(i => i.DurationSeconds ?? 0);
        var output = ctx.NewGroupFile(ext, "merged");

        if (format == "auto" && ConcatPlanner.CanStreamCopyAudio(infos, extensions))
        {
            var list = Path.Combine(ctx.TempDirectory, "inputs.txt");
            await File.WriteAllTextAsync(list, VideoToolArgs.ConcatList(inputs.Select(Path.GetFullPath)), new UTF8Encoding(false), ctx.Ct).ConfigureAwait(false);
            var result = await ctx.RunFfmpegAsync(AudioToolArgs.MergeCopy(list, output), total, label: "Joining").ConfigureAwait(false);
            if (result.Success)
            {
                MediaContext.RequireOutput(output);
                return [output];
            }
            if (File.Exists(output)) File.Delete(output);
        }

        var command = AudioToolArgs.MergeFilter(inputs, infos, output, ext, await ctx.EncodersAsync().ConfigureAwait(false));
        if (!command.Ok) throw new MediaFailure(command.Error!);
        await ctx.RunFfmpegOrFailAsync(command.Args, total, label: "Re-encoding").ConfigureAwait(false);
        MediaContext.RequireOutput(output);
        return [output];
    }
}
