using System.Text;
using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>Pure command builders for the stream-copy and analysis style video tools.</summary>
internal static class VideoToolArgs
{
    /// <summary>
    /// <c>.m4v</c> is a raw MPEG-4 video muxer in FFmpeg, not the iTunes MP4 variant people mean. Force the MP4 muxer for it.
    /// </summary>
    public static string[] ContainerFlags(string extension) => extension == "m4v" ? ["-f", "mp4"] : [];

    // ------------------------------------------------------------ mute

    /// <summary>Copies video (and subtitles) untouched and leaves the audio out. No re-encode, so it is instant and lossless.</summary>
    public static IReadOnlyList<string> Mute(string input, string output, string extension)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-i", input, "-map", "0:v", "-map", "0:s?", "-c", "copy", "-map_metadata", "0", "-map_chapters", "0"]);
        a.AddRange(ContainerFlags(extension));
        a.Add(output);
        return a;
    }

    // ------------------------------------------------------------ metadata

    /// <summary>
    /// Rewrites container tags without touching the streams. With <paramref name="strip"/> all global tags and chapters are removed
    /// and the muxer is told not to add its own "encoder" tag. Otherwise non-empty values replace the matching tag and everything else stays.
    /// </summary>
    public static IReadOnlyList<string> Metadata(string input, string output, string extension, string? title, string? artist, string? comment, bool strip)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-i", input, "-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy"]);
        if (strip)
        {
            a.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact"]);
        }
        else
        {
            a.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
            AddTag(a, "title", title);
            AddTag(a, "artist", artist);
            AddTag(a, "comment", comment);
        }
        a.AddRange(ContainerFlags(extension));
        a.Add(output);
        return a;
    }

    internal static void AddTag(List<string> args, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) args.AddRange(["-metadata", $"{key}={value.Trim()}"]);
    }

    // ------------------------------------------------------------ trim (stream copy)

    /// <summary>
    /// Cuts by stream copy: no quality loss and no waiting, but video can only start on a <b>keyframe</b>. With <c>-ss</c> before the
    /// input FFmpeg seeks to the keyframe at or before <paramref name="start"/>, so the clip can begin up to one GOP (typically 1-5 s) earlier than
    /// asked, and the end can overshoot the same way. Use the re-encode option for frame-accurate cuts.
    /// </summary>
    public static IReadOnlyList<string> TrimCopy(string input, string output, string extension, double start, double? end)
    {
        var a = FfmpegCommand.Global();
        if (start > 0) a.AddRange(["-ss", TimeSpec.Format(start)]);
        a.AddRange(["-i", input]);
        if (end is { } e) a.AddRange(["-t", TimeSpec.Format(e - start)]);
        a.AddRange(["-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-map_metadata", "0"]);
        a.AddRange(ContainerFlags(extension));
        a.Add(output);
        return a;
    }

    // ------------------------------------------------------------ snapshots

    /// <summary>One full-resolution PNG at <paramref name="time"/> (accurate seek: FFmpeg decodes from the previous keyframe).</summary>
    public static IReadOnlyList<string> SnapshotAt(string input, string outputPng, double time)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-ss", TimeSpec.Format(time), "-i", input, "-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-update", "1", outputPng]);
        return a;
    }

    /// <summary>
    /// One PNG every <paramref name="everySeconds"/> seconds, starting at 0. <paramref name="outputPattern"/> must contain
    /// <c>%05d</c> and have any other <c>%</c> doubled (see <see cref="FfmpegCommand.EscapePattern"/>).
    /// </summary>
    public static IReadOnlyList<string> SnapshotEvery(string input, string outputPattern, double everySeconds)
    {
        var rate = Math.Abs(everySeconds - Math.Round(everySeconds)) < 1e-9 ? $"1/{(long)Math.Round(everySeconds)}" : TimeSpec.Num(1 / everySeconds, 6);
        var a = FfmpegCommand.Global();
        a.AddRange(["-i", input, "-map", "0:v:0", "-vf", $"fps={rate}", "-an", "-sn", outputPattern]);
        return a;
    }

    // ------------------------------------------------------------ split

    /// <summary>
    /// Splits with the segment muxer in one pass. Stream copy cuts at the first keyframe at or after each split time, so clips never
    /// overlap or repeat footage (the price: a clip can start slightly later than the exact time). With <paramref name="reencode"/> the
    /// caller builds the encode arguments and <see cref="SegmentOutput"/> supplies the muxer part instead.
    /// </summary>
    public static IReadOnlyList<string> SplitCopy(string input, string outputPattern, string extension, IReadOnlyList<double>? splitTimes, double? segmentSeconds, string? listFile = null)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-i", input, "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-sn", "-map_metadata", "0"]);
        a.AddRange(SegmentOutput(outputPattern, extension, splitTimes, segmentSeconds, forceKeyframes: false, listFile));
        return a;
    }

    /// <summary>Muxer options and output name for the segment muxer. Goes last on the command line.</summary>
    /// <param name="listFile">When set, FFmpeg also writes a CSV list there: each clip's file name, real start and end time (<c>-segment_list</c>).</param>
    public static IReadOnlyList<string> SegmentOutput(string outputPattern, string extension, IReadOnlyList<double>? splitTimes, double? segmentSeconds, bool forceKeyframes, string? listFile = null)
    {
        var a = new List<string>();
        if (splitTimes is { Count: > 0 })
        {
            var times = string.Join(",", splitTimes.Select(TimeSpec.Format));
            if (forceKeyframes) a.AddRange(["-force_key_frames", times]);
            a.AddRange(["-f", "segment", "-segment_times", times]);
        }
        else if (segmentSeconds is { } seg)
        {
            if (forceKeyframes) a.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{TimeSpec.Num(seg, 3)})"]);
            a.AddRange(["-f", "segment", "-segment_time", TimeSpec.Format(seg)]);
        }
        else
        {
            throw new ArgumentException("Give split times or a segment length.");
        }
        a.AddRange(["-reset_timestamps", "1", "-segment_start_number", "1"]);
        if (listFile is not null) a.AddRange(["-segment_list", listFile, "-segment_list_type", "csv"]);
        if (extension == "m4v") a.AddRange(["-segment_format", "mp4"]);
        if (extension is "mp4" or "mov" or "m4v") a.AddRange(["-segment_format_options", "movflags=+faststart"]);
        a.Add(outputPattern);
        return a;
    }

    // ------------------------------------------------------------ join

    /// <summary>The list file for the concat demuxer: absolute paths, forward slashes, single quotes escaped as <c>'\''</c>.</summary>
    public static string ConcatList(IEnumerable<string> paths)
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var path in paths)
            sb.Append("file '").Append(path.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
        return sb.ToString();
    }

    /// <summary>
    /// Joins clips cut from one video (same codecs, so nothing is re-encoded) with the concat demuxer into the same container.
    /// <c>+faststart</c> is only passed to the MP4 family, the only muxer that understands it.
    /// </summary>
    public static IReadOnlyList<string> ConcatCopy(string listFile, string output, string extension)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-f", "concat", "-safe", "0", "-i", listFile, "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-map_metadata", "0"]);
        if (extension is "mp4" or "mov" or "m4v") a.AddRange(["-movflags", "+faststart"]);
        if (extension == "m4v") a.AddRange(["-f", "mp4"]);
        a.Add(output);
        return a;
    }

    public static IReadOnlyList<string> JoinCopy(string listFile, string output)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-f", "concat", "-safe", "0", "-i", listFile, "-map", "0:v", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart", "-map_metadata", "0", output]);
        return a;
    }

    /// <summary>
    /// Re-encoding join: every input is scaled (never stretched; black bars fill the rest) to the first input's size and frame rate
    /// and given 48 kHz stereo audio (silence for inputs without sound), then the <c>concat</c> filter stitches them together.
    /// </summary>
    public static BuiltCommand JoinFilter(IReadOnlyList<string> inputs, IReadOnlyList<MediaInfo> infos, string output, FfmpegEncoders encoders, int width, int height, double fps)
    {
        var encode = H264Aac(encoders, crf: 20, preset: "fast", audioKbps: 192);
        if (!encode.Ok) return encode;

        var anyAudio = infos.Any(i => i.HasAudio);
        var graph = new StringBuilder();
        var args = FfmpegCommand.Global();
        foreach (var input in inputs) args.AddRange(["-i", input]);

        var fpsText = TimeSpec.Num(Math.Clamp(fps, 1, 60), 3);
        for (var i = 0; i < inputs.Count; i++)
        {
            graph.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={fpsText},format=yuv420p[v{i}];");
            if (!anyAudio) continue;
            if (infos[i].HasAudio)
                graph.Append($"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
            else
                graph.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={TimeSpec.Num(infos[i].DurationSeconds ?? 1, 3)},asetpts=PTS-STARTPTS[a{i}];");
        }
        for (var i = 0; i < inputs.Count; i++)
            graph.Append(anyAudio ? $"[v{i}][a{i}]" : $"[v{i}]");
        graph.Append($"concat=n={inputs.Count}:v=1:a={(anyAudio ? 1 : 0)}[v]");
        if (anyAudio) graph.Append("[a]");

        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]"]);
        if (anyAudio) args.AddRange(["-map", "[a]"]);
        args.AddRange(encode.Args);
        if (!anyAudio) args.Add("-an");
        args.AddRange(["-movflags", "+faststart", output]);
        return new BuiltCommand(args, encode.Warnings);
    }

    /// <summary>Fixed H.264 + AAC settings for tools that build their own filter graph (join, audio visualiser).</summary>
    public static BuiltCommand H264Aac(FfmpegEncoders encoders, int crf, string preset, int audioKbps)
    {
        if (!encoders.Has("libx264"))
            return BuiltCommand.Fail("This FFmpeg build has no H.264 encoder (libx264). Install a full (GPL) FFmpeg build.");
        return new BuiltCommand(
            ["-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", $"{audioKbps}k"],
            []);
    }
}

/// <summary>Decides whether inputs can be joined by copying their streams.</summary>
internal static class ConcatPlanner
{
    private static readonly HashSet<string> Mp4Video = ["h264", "hevc", "av1", "mpeg4"];
    private static readonly HashSet<string> Mp4Audio = ["aac", "mp3", "ac3", "eac3", "opus", "alac"];

    /// <summary>
    /// True when every input has the same video codec, size, pixel format and (within 1%) frame rate, the same audio layout, and
    /// the codecs fit in MP4. Anything else needs the re-encoding concat filter. This is a conservative test: a false "no" only costs time.
    /// </summary>
    public static bool CanStreamCopyIntoMp4(IReadOnlyList<MediaInfo> infos)
    {
        if (infos.Count < 2 || infos.Any(i => !i.HasVideo)) return false;
        var first = infos[0];
        var v0 = first.Video!;
        if (v0.CodecName is null || !Mp4Video.Contains(v0.CodecName)) return false;

        foreach (var info in infos.Skip(1))
        {
            var v = info.Video!;
            if (v.CodecName != v0.CodecName || v.Width != v0.Width || v.Height != v0.Height || v.PixelFormat != v0.PixelFormat || v.Rotation != v0.Rotation) return false;
            if (v.FrameRate is null || v0.FrameRate is null || Math.Abs(v.FrameRate.Value - v0.FrameRate.Value) / v0.FrameRate.Value > 0.01) return false;
            if (v.IsVariableFrameRate != v0.IsVariableFrameRate) return false;
            if (info.HasAudio != first.HasAudio) return false;
            if (info.HasAudio)
            {
                var a = info.Audio!;
                var a0 = first.Audio!;
                if (a.CodecName != a0.CodecName || a.SampleRate != a0.SampleRate || a.Channels != a0.Channels) return false;
            }
        }
        return !first.HasAudio || (first.Audio!.CodecName is { } ac && Mp4Audio.Contains(ac));
    }

    /// <summary>Audio-only version: same codec, sample rate and channels, and the same container so the concat demuxer can copy.</summary>
    public static bool CanStreamCopyAudio(IReadOnlyList<MediaInfo> infos, IReadOnlyList<string> extensions)
    {
        if (infos.Count < 2 || infos.Any(i => !i.HasAudio || i.HasVideo)) return false;
        if (extensions.Distinct().Count() != 1) return false;
        var a0 = infos[0].Audio!;
        if (a0.CodecName is null) return false;
        return infos.Skip(1).All(i => i.Audio!.CodecName == a0.CodecName && i.Audio.SampleRate == a0.SampleRate && i.Audio.Channels == a0.Channels);
    }

    /// <summary>Output size/rate for the re-encoding join: the first input as the viewer sees it, made even.</summary>
    public static (int Width, int Height, double Fps) TargetFormat(IReadOnlyList<MediaInfo> infos)
    {
        var v = infos[0].Video;
        var w = v?.DisplayWidth is > 0 ? v.DisplayWidth!.Value : 1280;
        var h = v?.DisplayHeight is > 0 ? v.DisplayHeight!.Value : 720;
        var fps = v?.FrameRate is > 0 ? v.FrameRate!.Value : 30;
        return (Math.Max(2, w - w % 2), Math.Max(2, h - h % 2), Math.Round(fps, 3));
    }
}
