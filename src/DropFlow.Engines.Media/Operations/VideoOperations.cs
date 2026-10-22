using System.Text;
using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.Engines.Media.Runtime;
using DropFlow.Engines.Media.Subtitles;

namespace DropFlow.Engines.Media.Operations;

/// <summary>Common shape of the single-input video tools. gif is excluded: the catalog files it under images, and a GIF has no tracks to edit.</summary>
internal abstract class VideoToolBase : MediaOperation
{
    public static readonly IReadOnlyList<string> Inputs = ["mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v"];
    public static readonly IReadOnlyList<string> Presets = EncoderPlanner.PresetNames;

    protected abstract string Id { get; }

    public override IReadOnlyCollection<string> Ids => [Id];

    public override bool Supports(string operation, string inputExtension) => operation == Id && Inputs.Contains(inputExtension);

    /// <summary>Base settings for tools that re-encode: software encoders, container from the input (modern ones) or MP4, audio matched to the container.</summary>
    protected static EncoderSettings Reencode(MediaContext ctx, int crf = 20, string preset = "fast")
    {
        var container = MediaContext.ContainerOf(MediaContext.ReencodeExtension(ctx.InputExtension));
        var webm = container == ContainerFormat.Webm;
        return new EncoderSettings
        {
            Container = container,
            VideoCodec = webm ? VideoCodec.Vp9 : VideoCodec.H264,
            Crf = webm ? Math.Min(63, crf + 8) : crf,
            Preset = preset,
            AudioCodec = webm ? AudioCodec.Opus : AudioCodec.Aac,
            AudioBitrateKbps = webm ? 128 : 192,
            Hardware = HardwarePreference.None,
        };
    }

    protected static string ReencodeExtension(MediaContext ctx) => MediaContext.ReencodeExtension(ctx.InputExtension);
}

/// <summary>
/// <b>vid.compress</b> — smaller file by re-encoding. Options: <c>crf</c> (16-38, default 26; higher = smaller; WebM uses crf+8, H.265 crf+5 (the same visual quality)),
/// <c>preset</c> (speed name, default medium), <c>codec</c> (h264 | h265, default h264), <c>maxHeight</c> (keep | 2160 | 1440 | 1080 | 720 | 480 | 360),
/// <c>audioBitrate</c> (kbps, default 128). Output: <c>name (compressed).ext</c>; AVI/WMV/FLV/M4V inputs are written as MP4.
/// </summary>
internal sealed class VideoCompressOperation : VideoToolBase
{
    protected override string Id => "vid.compress";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("crf", ToolOptionKind.Number, 26, Min: 16, Max: 38),
        new("preset", ToolOptionKind.Choice, "medium", Choices: Presets),
        new("codec", ToolOptionKind.Choice, "h264", Choices: ["h264", "h265"]),
        new("maxHeight", ToolOptionKind.Choice, "keep", Choices: ["keep", "2160", "1440", "1080", "720", "480", "360"]),
        new("audioBitrate", ToolOptionKind.Number, 128, Min: 32, Max: 320),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var crf = ctx.Int("crf", 26, 16, 38);
        var codec = ctx.Choice("codec", "h264", ["h264", "h265"]);
        var maxHeight = ctx.Choice("maxHeight", "keep", ["keep", "2160", "1440", "1080", "720", "480", "360"]);

        var settings = Reencode(ctx, crf, ctx.Choice("preset", "medium", Presets)) with
        {
            AudioBitrateKbps = ctx.Int("audioBitrate", 128, 32, 320),
            MaxHeight = maxHeight == "keep" ? null : int.Parse(maxHeight, System.Globalization.CultureInfo.InvariantCulture),
        };
        if (codec == "h265" && settings.Container != ContainerFormat.Webm) settings = settings with { VideoCodec = VideoCodec.H265, Crf = Math.Min(51, crf + 5) };

        var output = ctx.NewFile(ReencodeExtension(ctx), "compressed");
        await ctx.EncodeAsync(settings, info, output).ConfigureAwait(false);

        if (new FileInfo(output).Length >= new FileInfo(ctx.Input).Length)
            ctx.Progress.Report(new JobProgress(1, "The result is not smaller than the original. Try a higher CRF value or a lower maximum height."));
        return [output];
    }
}

/// <summary>
/// <b>vid.target-size</b> — hit a file size. Options: <c>sizeMb</c> (1-100000, default 25; 1 MB = 1,000,000 bytes), <c>audioBitrate</c> (kbps reserved for sound, default 96),
/// <c>codec</c> (h264 | h265), <c>preset</c> (default medium). Bitrate = size / duration minus audio, with 3% held back for container overhead, so the
/// result lands 1-4% under the target; software H.264/H.265/VP9 use two passes. If the budget leaves too few bits per pixel the resolution is lowered
/// (never below 240p). Fails if even that cannot fit.
/// </summary>
internal sealed class VideoTargetSizeOperation : VideoToolBase
{
    protected override string Id => "vid.target-size";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("sizeMb", ToolOptionKind.Number, 25, Min: 1, Max: 100000, Required: true),
        new("audioBitrate", ToolOptionKind.Number, 96, Min: 32, Max: 320),
        new("codec", ToolOptionKind.Choice, "h264", Choices: ["h264", "h265"]),
        new("preset", ToolOptionKind.Choice, "medium", Choices: Presets),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var settings = Reencode(ctx, 23, ctx.Choice("preset", "medium", Presets)) with
        {
            RateControl = RateControl.TargetSize,
            TargetSizeMb = ctx.Number("sizeMb", 25, 1, 100000),
            AudioBitrateKbps = ctx.Int("audioBitrate", 96, 32, 320),
            Crf = null,
        };
        if (ctx.Choice("codec", "h264", ["h264", "h265"]) == "h265" && settings.Container != ContainerFormat.Webm)
            settings = settings with { VideoCodec = VideoCodec.H265 };

        var output = ctx.NewFile(ReencodeExtension(ctx), "compressed");
        await ctx.EncodeAsync(settings, info, output).ConfigureAwait(false);
        return [output];
    }
}

/// <summary><b>vid.mute</b> — removes the audio track by stream copy (instant, lossless). No options. Output: <c>name (muted).ext</c>.</summary>
internal sealed class VideoMuteOperation : VideoToolBase
{
    protected override string Id => "vid.mute";

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        if (info is not null && !info.HasVideo) throw new MediaFailure("The file has no video stream.");
        var output = ctx.NewFile(ctx.InputExtension, "muted");
        await ctx.RunFfmpegOrFailAsync(VideoToolArgs.Mute(ctx.Input, output, ctx.InputExtension), info?.DurationSeconds).ConfigureAwait(false);
        MediaContext.RequireOutput(output);
        return [output];
    }
}

/// <summary>
/// <b>vid.metadata</b> — edit tags by stream copy. Options: <c>title</c>, <c>artist</c>, <c>comment</c> (empty = leave as is),
/// <c>strip</c> (true removes all tags and chapters and ignores the text fields). Output: <c>name (edited).ext</c>.
/// </summary>
internal sealed class VideoMetadataOperation : VideoToolBase
{
    protected override string Id => "vid.metadata";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("title", ToolOptionKind.Text, ""),
        new("artist", ToolOptionKind.Text, ""),
        new("comment", ToolOptionKind.Text, ""),
        new("strip", ToolOptionKind.Bool, false),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var strip = ctx.Flag("strip");
        var title = ctx.Text("title");
        var artist = ctx.Text("artist");
        var comment = ctx.Text("comment");
        if (!strip && title.Length == 0 && artist.Length == 0 && comment.Length == 0)
            throw new MediaFailure("Enter a title, artist or comment, or choose to remove all metadata.");

        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        var output = ctx.NewFile(ctx.InputExtension, "edited");
        await ctx.RunFfmpegOrFailAsync(VideoToolArgs.Metadata(ctx.Input, output, ctx.InputExtension, title, artist, comment, strip), info?.DurationSeconds).ConfigureAwait(false);
        MediaContext.RequireOutput(output);
        return [output];
    }
}

/// <summary>
/// <b>vid.trim</b> — keep a section. Options: <c>start</c> (seconds or hh:mm:ss, default 0), <c>end</c> (empty = to the end),
/// <c>reencode</c> (false by default). Without <c>reencode</c> the cut is a stream copy: instant and lossless, but video can only
/// start on a keyframe, so the clip may begin up to one keyframe interval (often 1-5 s) before <c>start</c> and the end can overshoot.
/// With <c>reencode</c> the cut is frame-accurate (slower, CRF 18). Output: <c>name (trimmed).ext</c>.
/// </summary>
internal sealed class VideoTrimOperation : VideoToolBase
{
    protected override string Id => "vid.trim";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("start", ToolOptionKind.Text, "0"),
        new("end", ToolOptionKind.Text, ""),
        new("reencode", ToolOptionKind.Bool, false),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var start = ctx.Time("start") ?? 0;
        var end = ctx.Time("end");
        if (end is { } e0 && e0 <= start) throw new MediaFailure("The end time must be after the start time.");

        var reencode = ctx.Flag("reencode");
        var info = reencode ? await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false) : await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        var duration = info?.DurationSeconds;
        if (duration is > 0)
        {
            if (start >= duration) throw new MediaFailure($"The start time is past the end of the video ({TimeSpec.FormatClock(duration.Value)} long).");
            if (end > duration) end = duration;
        }
        var length = (end ?? duration) - start;

        if (!reencode)
        {
            var output = ctx.NewFile(ctx.InputExtension, "trimmed");
            await ctx.RunFfmpegOrFailAsync(VideoToolArgs.TrimCopy(ctx.Input, output, ctx.InputExtension, start, end), length).ConfigureAwait(false);
            MediaContext.RequireOutput(output);
            return [output];
        }

        var settings = Reencode(ctx, 18) with
        {
            TrimStart = TimeSpan.FromSeconds(start),
            TrimEnd = end is { } e ? TimeSpan.FromSeconds(e) : null,
        };
        var target = ctx.NewFile(ReencodeExtension(ctx), "trimmed");
        await ctx.EncodeAsync(settings, info!, target).ConfigureAwait(false);
        return [target];
    }
}

/// <summary>
/// <b>vid.crop</b> — cut away the edges. Options: <c>aspect</c> (custom | 1:1 | 4:5 | 3:4 | 4:3 | 16:9 | 9:16 | 21:9, default 1:1; a preset crops the
/// largest centred area of that shape), <c>x</c>, <c>y</c>, <c>w</c>, <c>h</c> (pixels of the picture as displayed, used when aspect is custom).
/// Values are rounded down to even numbers. Audio is copied. Output: <c>name (cropped).ext</c>.
/// </summary>
internal sealed class VideoCropOperation : VideoToolBase
{
    public static readonly IReadOnlyList<string> Aspects = ["custom", "1:1", "4:5", "3:4", "4:3", "16:9", "9:16", "21:9"];

    protected override string Id => "vid.crop";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("aspect", ToolOptionKind.Choice, "1:1", Choices: Aspects),
        new("x", ToolOptionKind.Number, 0, Min: 0, Max: 16384),
        new("y", ToolOptionKind.Number, 0, Min: 0, Max: 16384),
        new("w", ToolOptionKind.Number, 0, Min: 0, Max: 16384),
        new("h", ToolOptionKind.Number, 0, Min: 0, Max: 16384),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var video = info.Video ?? throw new MediaFailure("The file has no video stream.");
        if (video.DisplayWidth is not > 0 || video.DisplayHeight is not > 0) throw new MediaFailure("The video size could not be read.");
        int frameW = video.DisplayWidth!.Value, frameH = video.DisplayHeight!.Value;

        var aspect = ctx.Choice("aspect", "1:1", Aspects);
        PixelRect rect;
        if (aspect == "custom")
        {
            var x = ctx.Int("x", 0, 0);
            var y = ctx.Int("y", 0, 0);
            var w = ctx.Int("w", 0, 0);
            var h = ctx.Int("h", 0, 0);
            if (w <= 0 || h <= 0) throw new MediaFailure("Enter the width and height of the area to keep.");
            if (x + w > frameW || y + h > frameH)
                throw new MediaFailure($"The crop area ({w}x{h} at {x},{y}) does not fit inside the {frameW}x{frameH} picture.");
            rect = new PixelRect(ToolFilters.Even(x), ToolFilters.Even(y), Math.Max(2, ToolFilters.Even(w)), Math.Max(2, ToolFilters.Even(h)));
        }
        else
        {
            ToolFilters.TryParseAspect(aspect, out var aw, out var ah);
            rect = ToolFilters.CenteredAspectCrop(frameW, frameH, aw, ah);
        }

        var settings = Reencode(ctx, 18) with { AudioCodec = AudioCodec.Copy, VideoFilters = [ToolFilters.Crop(rect)] };
        var output = ctx.NewFile(ReencodeExtension(ctx), "cropped");
        await ctx.EncodeAsync(settings, info, output).ConfigureAwait(false);
        return [output];
    }
}

/// <summary>
/// <b>vid.speed</b> — faster or slower playback, pitch kept. Option: <c>speed</c> (0.25-4, default 2). Video timestamps are rescaled
/// (<c>setpts</c>) and audio is time-stretched with a chain of <c>atempo</c> stages (each within 0.5-2, so 4x is two stages). Output: <c>name (speed 2x).ext</c>.
/// </summary>
internal sealed class VideoSpeedOperation : VideoToolBase
{
    protected override string Id => "vid.speed";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("speed", ToolOptionKind.Number, 2.0, Min: 0.25, Max: 4),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var speed = ctx.Number("speed", 2, 0.25, 4);
        if (Math.Abs(speed - 1) < 1e-9) throw new MediaFailure("1x is the original speed; choose a different value.");

        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var settings = Reencode(ctx) with
        {
            VideoFilters = [ToolFilters.SetPts(speed)],
            AudioFilters = ToolFilters.AtempoChain(speed) is { } chain ? [chain] : [],
        };
        var output = ctx.NewFile(ReencodeExtension(ctx), $"speed {TimeSpec.Num(speed, 3)}x");
        var seconds = info.DurationSeconds is > 0 ? info.DurationSeconds / speed : null;
        await ctx.EncodeAsync(settings, info, output, expectedSecondsOverride: seconds).ConfigureAwait(false);
        return [output];
    }
}

/// <summary>
/// <b>vid.snapshot</b> — save frames as full-resolution PNG files into a new folder <c>name (frames)</c>. Options: <c>times</c> (list such as
/// "5, 1:10, 90.5"; default "0"), <c>every</c> (a frame every N seconds starting at 0; 0 = off). Times past the end are skipped.
/// The result is the folder.
/// </summary>
internal sealed class VideoSnapshotOperation : VideoToolBase
{
    protected override string Id => "vid.snapshot";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("times", ToolOptionKind.Text, "0"),
        new("every", ToolOptionKind.Number, 0, Min: 0, Max: 3600),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        IReadOnlyList<double> times;
        try
        {
            times = TimeSpec.ParseList(ctx.Text("times", "0"));
        }
        catch (FormatException ex)
        {
            throw new MediaFailure(ex.Message);
        }
        var every = ctx.Number("every", 0, 0, 3600);
        times = times.Distinct().OrderBy(t => t).ToList();
        if (times.Count == 0 && every <= 0) times = [0];

        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        if (info is not null && !info.HasVideo) throw new MediaFailure("The file has no video stream.");
        var duration = info?.DurationSeconds;

        var folder = ctx.NewFolder("frames");
        var units = times.Count + (every > 0 ? 1 : 0);
        var done = 0;

        foreach (var t in times)
        {
            if (duration is > 0 && t >= duration) { done++; continue; }
            var file = Path.Combine(folder, $"{ctx.InputStem} {TimeSpec.FormatClock(t, forFileName: true)}.png");
            await ctx.RunFfmpegOrFailAsync(VideoToolArgs.SnapshotAt(ctx.Input, file, t), 0.04, done / (double)units, (done + 1) / (double)units).ConfigureAwait(false);
            done++;
        }

        if (every > 0)
        {
            var pattern = FfmpegCommand.EscapePattern(Path.Combine(folder, ctx.InputStem + " frame ")) + "%05d.png";
            await ctx.RunFfmpegOrFailAsync(VideoToolArgs.SnapshotEvery(ctx.Input, pattern, every), duration, done / (double)units, 1).ConfigureAwait(false);
        }

        if (!Directory.EnumerateFiles(folder, "*.png").Any())
            throw new MediaFailure("No frames were saved: the requested times are past the end of the video.");
        return [folder];
    }
}

/// <summary>
/// <b>vid.split</b> — cut into clips inside a new folder <c>name (clips)</c>. Options: <c>points</c> (split times, "1:30, 3:00"), <c>segment</c> (clip length in seconds, used when
/// <c>points</c> is empty), <c>reencode</c> (false by default), <c>remove</c> (clip numbers to leave out, counted from 1: "2, 4"), <c>output</c> (clips | join, default clips).
/// Stream copy (default) splits on the first keyframe at or after each point, so no footage is repeated or lost but a clip can start slightly late; <c>reencode</c> forces a keyframe
/// at every point for exact cuts. Clips are named <c>name part 001.ext</c> and keep their planned numbers when some are removed. With <c>output</c> = join the clips that are not removed
/// are joined again by stream copy into <c>name (edited).ext</c> (cutting the removed parts out of the video). The result is the folder, or that file.
/// <para>
/// When clips are removed the segment muxer writes into the scratch folder with a CSV list (<c>-segment_list</c>) of where each clip really starts, and each produced clip is matched
/// to its planned number by that start time, so keyframe drift cannot shift the numbering.
/// </para>
/// </summary>
internal sealed class VideoSplitOperation : VideoToolBase
{
    protected override string Id => "vid.split";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("points", ToolOptionKind.Text, "", Required: false),
        new("segment", ToolOptionKind.Number, 0, Min: 0, Max: 86400),
        new("reencode", ToolOptionKind.Bool, false),
        new("remove", ToolOptionKind.Text, ""),
        new("output", ToolOptionKind.Choice, "clips", Choices: ["clips", "join"]),
    ];

    /// <summary>"2, 4" to [2, 4]: whole numbers from 1, Arabic-Indic digits allowed, duplicates dropped. Throws <see cref="MediaFailure"/> on anything else.</summary>
    internal static IReadOnlyList<int> ParseClipNumbers(string text)
    {
        var result = new SortedSet<int>();
        foreach (var token in TimeSpec.NormalizeDigits(text).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
                throw new MediaFailure($"'{token}' is not a clip number. Clips are numbered from 1.");
            result.Add(n);
        }
        return [.. result];
    }

    /// <summary>The planned (1-based) clip a produced clip belongs to, from the time it really starts.</summary>
    internal static int PlannedClip(double start, IReadOnlyList<double> points, double? segment)
    {
        const double tolerance = 0.05;
        if (points.Count > 0) return 1 + points.Count(p => p <= start + tolerance);
        return 1 + (int)Math.Floor((start + tolerance) / segment!.Value);
    }

    /// <summary>Reads FFmpeg's segment CSV list ("clip001.mp4,0.000000,60.000000" per line) into (file name, start).</summary>
    internal static IReadOnlyList<(string File, double Start)> ParseSegmentList(string csv)
    {
        var list = new List<(string, double)>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            var name = string.Join(",", parts[..^2]).Trim('"');
            if (double.TryParse(parts[^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var start))
                list.Add((name, start));
        }
        return list;
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        IReadOnlyList<double> points;
        try
        {
            points = TimeSpec.ParseList(ctx.Text("points")).Distinct().OrderBy(t => t).ToList();
        }
        catch (FormatException ex)
        {
            throw new MediaFailure(ex.Message);
        }
        var segment = ctx.Number("segment", 0, 0, 86400);
        if (points.Count == 0 && segment <= 0)
            throw new MediaFailure("Enter split times (for example 1:30, 3:00) or a clip length in seconds.");
        if (points.Any(p => p <= 0)) throw new MediaFailure("Split times must be greater than zero.");

        var remove = ParseClipNumbers(ctx.Text("remove"));
        var join = ctx.Choice("output", "clips", ["clips", "join"]) == "join";
        if (join && remove.Count == 0) throw new MediaFailure("Choose the clips to remove before joining the rest, or save the clips instead.");

        var reencode = ctx.Flag("reencode");
        var needInfo = reencode || (remove.Count > 0 && points.Count == 0);
        var info = needInfo ? await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false) : await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        var duration = info?.DurationSeconds;
        if (duration is > 0)
        {
            if (points.Count > 0 && points[^1] >= duration)
                throw new MediaFailure($"The split time {TimeSpec.FormatClock(points[^1])} is past the end of the video ({TimeSpec.FormatClock(duration.Value)} long).");
            if (points.Count == 0 && segment >= duration)
                throw new MediaFailure("The clip length is not shorter than the video, so there is nothing to split.");
        }

        double? segmentSeconds = points.Count == 0 ? segment : null;
        if (remove.Count > 0)
        {
            int? planned = points.Count > 0 ? points.Count + 1 : duration is > 0 ? (int)Math.Ceiling(duration.Value / segment - 1e-6) : null;
            if (planned is { } count && remove[^1] > count)
                throw new MediaFailure($"There is no clip {remove[^1]}: the video is cut into {count} clips.");
            if (planned is { } all && remove.Count >= all)
                throw new MediaFailure("Every clip is marked for removal, so nothing would be left.");
        }

        var ext = reencode ? ReencodeExtension(ctx) : ctx.InputExtension;
        if (remove.Count == 0)
        {
            var folder = ctx.NewFolder("clips");
            var pattern = FfmpegCommand.EscapePattern(Path.Combine(folder, ctx.InputStem + " part ")) + "%03d." + ext;
            await ctx.RunFfmpegOrFailAsync(await SegmentArgsAsync(ctx, info, reencode, pattern, ext, points, segmentSeconds, null).ConfigureAwait(false), duration).ConfigureAwait(false);
            if (!Directory.EnumerateFiles(folder).Any()) throw new MediaFailure("FFmpeg finished but produced no clips.");
            return [folder];
        }

        // Clips are removed: cut into the scratch folder with a list of where each clip really starts, then keep or join the rest.
        var work = Path.Combine(ctx.TempDirectory, "clips");
        Directory.CreateDirectory(work);
        var listFile = Path.Combine(work, "clips.csv");
        var scratchPattern = Path.Combine(work, "clip%03d." + ext);
        var cutEnd = join ? 0.8 : 0.95;
        await ctx.RunFfmpegOrFailAsync(await SegmentArgsAsync(ctx, info, reencode, scratchPattern, ext, points, segmentSeconds, listFile).ConfigureAwait(false), duration, 0, cutEnd).ConfigureAwait(false);
        if (!File.Exists(listFile)) throw new MediaFailure("FFmpeg finished but did not report the clips it made.");

        var kept = ParseSegmentList(await File.ReadAllTextAsync(listFile, ctx.Ct).ConfigureAwait(false))
            .Select(s => (Path: Path.Combine(work, Path.GetFileName(s.File)), Number: PlannedClip(s.Start, points, segmentSeconds)))
            .Where(s => File.Exists(s.Path) && !remove.Contains(s.Number))
            .ToList();
        if (kept.Count == 0) throw new MediaFailure("Every clip FFmpeg made is marked for removal, so nothing would be left.");

        if (!join)
        {
            var folder = ctx.NewFolder("clips");
            foreach (var (path, number) in kept)
            {
                var target = Path.Combine(folder, $"{ctx.InputStem} part {number:000}.{ext}");
                for (var n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{ctx.InputStem} part {number:000}-{n}.{ext}");
                File.Move(path, target);
            }
            ctx.Progress.Report(new JobProgress(1));
            return [folder];
        }

        var output = ctx.NewFile(ext, "edited");
        var concat = Path.Combine(work, "kept.txt");
        await File.WriteAllTextAsync(concat, VideoToolArgs.ConcatList(kept.Select(k => k.Path)), new UTF8Encoding(false), ctx.Ct).ConfigureAwait(false);
        var keptSeconds = duration is > 0 ? duration * kept.Count / (kept.Count + remove.Count) : null;
        await ctx.RunFfmpegOrFailAsync(VideoToolArgs.ConcatCopy(concat, output, ext), keptSeconds, cutEnd, 1, label: "Joining").ConfigureAwait(false);
        MediaContext.RequireOutput(output);
        return [output];
    }

    private static async Task<IReadOnlyList<string>> SegmentArgsAsync(
        MediaContext ctx, MediaInfo? info, bool reencode, string pattern, string ext, IReadOnlyList<double> points, double? segmentSeconds, string? listFile)
    {
        if (!reencode) return VideoToolArgs.SplitCopy(ctx.Input, pattern, ext, points, segmentSeconds, listFile);

        var settings = Reencode(ctx, 20) with { FastStart = false }; // the segment muxer takes movflags through -segment_format_options instead
        var plan = EncoderPlanner.Plan(settings, info!, new PlanContext { Encoders = await ctx.EncodersAsync().ConfigureAwait(false) });
        if (!plan.Ok) throw new MediaFailure(plan.Error!);
        return [.. plan.Passes[0].Take(plan.Passes[0].Count - 1), .. VideoToolArgs.SegmentOutput(pattern, ext, points, segmentSeconds, forceKeyframes: true, listFile)];
    }
}

/// <summary>
/// <b>vid.join</b> (multi-file) — one MP4 from several videos, in the order given. Option: <c>mode</c> (auto | copy | reencode, default auto).
/// auto copies the streams (instant, lossless, concat demuxer) when every file has the same codec, size, frame rate and audio format, and otherwise re-encodes with the concat filter:
/// each clip is scaled (never stretched) with black bars to the first clip's size and frame rate, audio is brought to 48 kHz stereo, and clips without sound get silence.
/// If a copy join fails in auto mode it falls back to re-encoding. Output: <c>first name (joined).mp4</c>.
/// </summary>
internal sealed class VideoJoinOperation : VideoToolBase
{
    protected override string Id => "vid.join";

    public override bool IsMulti => true;

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("mode", ToolOptionKind.Choice, "auto", Choices: ["auto", "copy", "reencode"]),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var inputs = ctx.Job.Inputs;
        var infos = new List<MediaInfo>();
        foreach (var input in inputs)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var info = await ctx.ProbeAsync(input).ConfigureAwait(false);
            if (!info.HasVideo) throw new MediaFailure($"\"{Path.GetFileName(input)}\" has no video stream.");
            infos.Add(info);
        }

        var mode = ctx.Choice("mode", "auto", ["auto", "copy", "reencode"]);
        var total = infos.Sum(i => i.DurationSeconds ?? 0);
        var output = ctx.NewGroupFile("mp4", "joined");

        if (mode != "reencode" && (mode == "copy" || ConcatPlanner.CanStreamCopyIntoMp4(infos)))
        {
            var list = Path.Combine(ctx.TempDirectory, "inputs.txt");
            await File.WriteAllTextAsync(list, VideoToolArgs.ConcatList(inputs.Select(Path.GetFullPath)), new UTF8Encoding(false), ctx.Ct).ConfigureAwait(false);
            var result = await ctx.RunFfmpegAsync(VideoToolArgs.JoinCopy(list, output), total, 0, 1, label: "Joining").ConfigureAwait(false);
            if (result.Success)
            {
                MediaContext.RequireOutput(output);
                return [output];
            }
            if (mode == "copy") throw new MediaFailure(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
            if (File.Exists(output)) File.Delete(output); // -n would refuse to overwrite the partial file
            ctx.Progress.Report(new JobProgress(0, "The clips cannot be joined without re-encoding; re-encoding."));
        }

        var (width, height, fps) = ConcatPlanner.TargetFormat(infos);
        var command = VideoToolArgs.JoinFilter(inputs, infos, output, await ctx.EncodersAsync().ConfigureAwait(false), width, height, fps);
        if (!command.Ok) throw new MediaFailure(command.Error!);
        await ctx.RunFfmpegOrFailAsync(command.Args, total, 0, 1, label: "Re-encoding").ConfigureAwait(false);
        MediaContext.RequireOutput(output);
        return [output];
    }
}

/// <summary>
/// <b>vid.redact</b> — hide a rectangle. Options: <c>x</c>, <c>y</c>, <c>w</c>, <c>h</c> (pixels of the displayed picture; <c>w</c>/<c>h</c> required), <c>mode</c> (blur | solid, default blur),
/// <c>strength</c> (blur radius 2-60, default 20), <c>color</c> (solid fill: name or #RRGGBB, default black), <c>start</c> and <c>end</c> (time range; both empty = the whole video).
/// Audio is copied. Output: <c>name (redacted).ext</c>.
/// </summary>
internal sealed class VideoRedactOperation : VideoToolBase
{
    protected override string Id => "vid.redact";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("x", ToolOptionKind.Number, 0, Min: 0, Max: 16384),
        new("y", ToolOptionKind.Number, 0, Min: 0, Max: 16384),
        new("w", ToolOptionKind.Number, 0, Min: 0, Max: 16384, Required: true),
        new("h", ToolOptionKind.Number, 0, Min: 0, Max: 16384, Required: true),
        new("mode", ToolOptionKind.Choice, "blur", Choices: ["blur", "solid"]),
        new("strength", ToolOptionKind.Number, 20, Min: 2, Max: 60),
        new("color", ToolOptionKind.Text, "black"),
        new("start", ToolOptionKind.Text, ""),
        new("end", ToolOptionKind.Text, ""),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);
        var video = info.Video ?? throw new MediaFailure("The file has no video stream.");
        int frameW = video.DisplayWidth ?? 0, frameH = video.DisplayHeight ?? 0;

        var x = ctx.Int("x", 0, 0);
        var y = ctx.Int("y", 0, 0);
        var w = ctx.Int("w", 0, 0);
        var h = ctx.Int("h", 0, 0);
        if (w <= 0 || h <= 0) throw new MediaFailure("Enter the width and height of the area to hide.");
        if (frameW > 0 && frameH > 0 && (x + w > frameW || y + h > frameH))
            throw new MediaFailure($"The area ({w}x{h} at {x},{y}) does not fit inside the {frameW}x{frameH} picture.");

        var start = ctx.Time("start");
        var end = ctx.Time("end");
        if (start is not null && end is not null && end <= start) throw new MediaFailure("The end time must be after the start time.");

        var rect = new PixelRect(ToolFilters.Even(x), ToolFilters.Even(y), Math.Max(2, ToolFilters.Even(w)), Math.Max(2, ToolFilters.Even(h)));
        string filter;
        if (ctx.Choice("mode", "blur", ["blur", "solid"]) == "blur")
        {
            filter = ToolFilters.RedactBlur(rect, ctx.Int("strength", 20, 2, 60), start, end);
        }
        else
        {
            var color = ctx.Text("color", "black");
            if (!ToolFilters.IsValidColor(color)) throw new MediaFailure($"'{color}' is not a colour. Use a name such as black or a code such as #FF0000.");
            filter = ToolFilters.RedactSolid(rect, color.StartsWith('#') ? "0x" + color[1..] : color, start, end);
        }

        var settings = Reencode(ctx, 18) with { AudioCodec = AudioCodec.Copy, VideoFilters = [filter] };
        var output = ctx.NewFile(ReencodeExtension(ctx), "redacted");
        await ctx.EncodeAsync(settings, info, output).ConfigureAwait(false);
        return [output];
    }
}

/// <summary>
/// <b>vid.burn-subs</b> — draw subtitles into the picture (libass). Options: <c>subtitle</c> (path to an .srt, .vtt, .ass or .ssa file; empty = look beside the video for
/// a file with the same name, such as clip.srt or clip.ar.srt), <c>fontSize</c> (8-48, default 18, relative to a 288-line picture so it scales with the resolution),
/// <c>position</c> (bottom | middle | top). SRT/VTT get white text with a black outline in Arial (which covers Arabic); an ASS file keeps its own styling.
/// The subtitle is converted to clean UTF-8 in a scratch folder first (so any encoding works) and handed to FFmpeg by a relative name,
/// and the Windows fonts folder is passed as <c>fontsdir</c> (escaped by <see cref="FilterEscaping"/>). Audio is copied. Output: <c>name (subtitled).ext</c>.
/// </summary>
internal sealed class VideoBurnSubsOperation : VideoToolBase
{
    private static readonly string[] SubtitleExtensions = ["srt", "ass", "ssa", "vtt"];

    protected override string Id => "vid.burn-subs";

    public override IReadOnlyList<ToolOption> OptionsFor(string operation) =>
    [
        new("subtitle", ToolOptionKind.Text, ""),
        new("fontSize", ToolOptionKind.Number, 18, Min: 8, Max: 48),
        new("position", ToolOptionKind.Choice, "bottom", Choices: ["bottom", "middle", "top"]),
    ];

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var subtitle = ctx.Text("subtitle");
        if (subtitle.Length == 0)
            subtitle = FindSidecar(ctx.Input) ?? throw new MediaFailure("No subtitle file was chosen and none with the video's name was found beside it.");
        if (!File.Exists(subtitle)) throw new MediaFailure($"The subtitle file was not found: {subtitle}");

        var ext = Path.GetExtension(subtitle).TrimStart('.').ToLowerInvariant();
        if (!SubtitleExtensions.Contains(ext)) throw new MediaFailure("Subtitles must be an .srt, .vtt, .ass or .ssa file.");

        var info = await ctx.ProbeAsync(ctx.Input).ConfigureAwait(false);

        // Normalise into the scratch folder: valid UTF-8, plain ASCII name, no escaping hazards left for the filter graph.
        var work = ctx.TempDirectory;
        string localName;
        try
        {
            var bytes = await File.ReadAllBytesAsync(subtitle, ctx.Ct).ConfigureAwait(false);
            if (SubtitleDecoder.LooksBinary(bytes)) throw new SubtitleFormatException("The subtitle file is binary data, not text.");
            if (ext is "ass" or "ssa")
            {
                var text = SubtitleDecoder.Decode(bytes, out _);
                if (!text.Contains("[Events]", StringComparison.OrdinalIgnoreCase)) throw new SubtitleFormatException("The ASS file has no [Events] section.");
                localName = "subs.ass";
                await File.WriteAllTextAsync(Path.Combine(work, localName), text, new UTF8Encoding(false), ctx.Ct).ConfigureAwait(false);
            }
            else
            {
                var doc = SubtitleParser.ParseBytes(bytes, SubtitleParser.FormatFromExtension(ext)!.Value);
                localName = "subs.srt";
                await File.WriteAllTextAsync(Path.Combine(work, localName), SubtitleWriter.Write(doc, SubtitleFormat.Srt, new SubtitleWriteOptions { NewLine = "\n" }), new UTF8Encoding(false), ctx.Ct).ConfigureAwait(false);
            }
        }
        catch (SubtitleFormatException ex)
        {
            throw new MediaFailure("The subtitle file could not be used: " + ex.Message);
        }

        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var style = localName.EndsWith(".srt", StringComparison.Ordinal)
            ? ToolFilters.BurnStyle(ctx.Int("fontSize", 18, 8, 48), ctx.Choice("position", "bottom", ["bottom", "middle", "top"]))
            : null;
        var filter = ToolFilters.Subtitles(localName, Directory.Exists(fonts) ? fonts : null, style);

        var settings = Reencode(ctx, 20) with { AudioCodec = AudioCodec.Copy, VideoFilters = [filter] };
        var output = ctx.NewFile(ReencodeExtension(ctx), "subtitled");
        var plan = EncoderPlanner.Plan(settings, info, await ctx.PlanContextAsync(output).ConfigureAwait(false));
        if (!plan.Ok) throw new MediaFailure(plan.Error!);

        // The subtitle is referenced by a relative name, so FFmpeg runs inside the scratch folder.
        var pass = plan.Passes[^1];
        var result = await ctx.RunFfmpegAsync(pass, plan.OutputDurationSeconds, workingDirectory: work, label: "Burning subtitles").ConfigureAwait(false);
        if (!result.Success) throw new MediaFailure(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));
        MediaContext.RequireOutput(output);
        return [output];
    }

    /// <summary>Looks beside the video for <c>name.srt</c>, <c>name.ass</c>, <c>name.vtt</c>, then <c>name.&lt;lang&gt;.srt</c> and the like.</summary>
    internal static string? FindSidecar(string videoPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(videoPath));
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (dir is null || !Directory.Exists(dir)) return null;

        var candidates = Directory.EnumerateFiles(dir, stem + ".*")
            .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f).TrimStart('.').ToLowerInvariant()))
            .OrderBy(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => Array.IndexOf(SubtitleExtensions, Path.GetExtension(f).TrimStart('.').ToLowerInvariant()))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.FirstOrDefault();
    }
}
