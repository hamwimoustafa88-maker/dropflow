using System.Text;
using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>Pure command builders for the audio tools.</summary>
internal static class AudioToolArgs
{
    // ------------------------------------------------------------ metadata

    /// <summary>
    /// Rewrites tags without re-encoding. Keeps embedded cover art where the format supports it (mp3, m4a, flac) unless
    /// <paramref name="strip"/> is set, which removes every tag, the cover picture, and the muxer's own "encoder" tag.
    /// </summary>
    public static IReadOnlyList<string> Metadata(string input, string output, string extension, MediaInfo? info, string? title, string? artist, string? album, string? comment, bool strip)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-i", input, "-map", "0:a"]);
        if (!strip && info?.CoverArt is { } cover && AudioFormats.CarriesCoverArt(extension)) a.AddRange(["-map", $"0:{cover.Index}"]);
        a.AddRange(["-c", "copy"]);
        if (strip)
        {
            a.AddRange(["-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact"]);
            if (extension == "mp3") a.AddRange(["-write_id3v1", "0", "-id3v2_version", "3"]);
        }
        else
        {
            a.AddRange(["-map_metadata", "0"]);
            VideoToolArgs.AddTag(a, "title", title);
            VideoToolArgs.AddTag(a, "artist", artist);
            VideoToolArgs.AddTag(a, "album", album);
            VideoToolArgs.AddTag(a, "comment", comment);
            if (extension == "mp3") a.AddRange(["-id3v2_version", "3", "-write_id3v1", "1"]);
        }
        a.Add(output);
        return a;
    }

    // ------------------------------------------------------------ trim

    /// <summary>
    /// Cuts by stream copy. Audio codecs are made of independent frames (MP3 ~26 ms, AAC ~21 ms), so unlike video the cut is
    /// accurate to a frame and nothing is re-encoded.
    /// </summary>
    public static IReadOnlyList<string> TrimCopy(string input, string output, string extension, MediaInfo? info, double start, double? end)
    {
        var a = FfmpegCommand.Global();
        if (start > 0) a.AddRange(["-ss", TimeSpec.Format(start)]);
        a.AddRange(["-i", input]);
        if (end is { } e) a.AddRange(["-t", TimeSpec.Format(e - start)]);
        a.AddRange(["-map", "0:a:0"]);
        if (info?.CoverArt is { } cover && AudioFormats.CarriesCoverArt(extension)) a.AddRange(["-map", $"0:{cover.Index}"]);
        a.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero", "-map_metadata", "0", output]);
        return a;
    }

    // ------------------------------------------------------------ analysis passes

    /// <summary>Runs an audio filter over the first audio stream and discards the result (loudnorm pass 1, silencedetect).</summary>
    public static IReadOnlyList<string> Analyze(string input, string filter)
    {
        var a = FfmpegCommand.Global(noOverwrite: false);
        a.AddRange(["-i", input, "-map", "0:a:0", "-vn", "-af", filter, "-f", "null", FfmpegCommand.NullDevice]);
        return a;
    }

    // ------------------------------------------------------------ channels

    /// <summary>
    /// The filter that converts to mono or stereo, or null when no filter is needed/possible. Mono to stereo duplicates the signal
    /// at full level (a plain <c>-ac 2</c> would attenuate each side by 3 dB); stereo to mono averages the channels.
    /// Surround sources use FFmpeg's standard down-mix via <c>aformat</c>.
    /// </summary>
    public static string? ChannelsFilter(string target, int sourceChannels)
    {
        if (target == "mono")
            return sourceChannels switch
            {
                1 => null,
                2 => "pan=mono|c0=0.5*c0+0.5*c1",
                _ => "aformat=channel_layouts=mono",
            };
        return sourceChannels switch
        {
            1 => "pan=stereo|c0=c0|c1=c0",
            2 => null,
            _ => "aformat=channel_layouts=stereo",
        };
    }

    // ------------------------------------------------------------ visualize

    /// <summary>
    /// Waveform video from audio: <c>showwaves</c> (centred lines, square-root scale so quiet speech is still visible) on black or
    /// over a still picture that is scaled to fill the frame. Ends with the audio (<c>-shortest</c>).
    /// </summary>
    public static BuiltCommand Visualize(string audioInput, string? backgroundImage, string output, string layout, string color, FfmpegEncoders encoders)
    {
        var encode = VideoToolArgs.H264Aac(encoders, crf: 23, preset: "fast", audioKbps: 192);
        if (!encode.Ok) return encode;

        var (w, h) = ToolFilters.VisualizeSize(layout);
        var wave = $"showwaves=s={w}x{h}:mode=cline:rate=25:colors={FilterEscaping.Value(color)}:scale=sqrt";

        var args = FfmpegCommand.Global();
        string graph;
        int audioIndex;
        if (backgroundImage is null)
        {
            args.AddRange(["-i", audioInput]);
            audioIndex = 0;
            graph = $"[0:a:0]{wave},format=yuv420p[v]";
        }
        else
        {
            args.AddRange(["-loop", "1", "-framerate", "25", "-i", backgroundImage, "-i", audioInput]);
            audioIndex = 1;
            graph = $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,format=yuv420p[bg];" +
                    $"[1:a:0]{wave},format=rgba[wf];[bg][wf]overlay=format=auto,format=yuv420p[v]";
        }

        args.AddRange(["-filter_complex", graph, "-map", "[v]", "-map", $"{audioIndex}:a:0"]);
        args.AddRange(encode.Args);
        args.AddRange(["-shortest", "-movflags", "+faststart", output]);
        return new BuiltCommand(args, []);
    }

    // ------------------------------------------------------------ merge

    public static IReadOnlyList<string> MergeCopy(string listFile, string output)
    {
        var a = FfmpegCommand.Global();
        a.AddRange(["-f", "concat", "-safe", "0", "-i", listFile, "-map", "0:a", "-c", "copy", "-map_metadata", "0", output]);
        return a;
    }

    /// <summary>
    /// Re-encoding merge with the concat filter. Every input is resampled to one rate and brought to one channel layout first,
    /// because the concat filter refuses inputs that differ.
    /// </summary>
    public static BuiltCommand MergeFilter(IReadOnlyList<string> inputs, IReadOnlyList<MediaInfo> infos, string output, string outputExtension, FfmpegEncoders encoders)
    {
        var sampleRate = infos[0].Audio?.SampleRate is > 0 ? infos[0].Audio!.SampleRate!.Value : 44100;
        var stereo = infos.Any(i => i.Audio?.Channels > 1);
        var layout = stereo ? "stereo" : "mono";

        var encoding = AudioFormats.Encode(outputExtension, null, AudioQuality.High, infos[0], encoders);
        if (!encoding.Ok) return BuiltCommand.Fail(encoding.Error!);

        var args = FfmpegCommand.Global();
        foreach (var input in inputs) args.AddRange(["-i", input]);

        var graph = new StringBuilder();
        for (var i = 0; i < inputs.Count; i++)
        {
            var upmix = stereo && infos[i].Audio?.Channels == 1 ? "pan=stereo|c0=c0|c1=c0," : "";
            graph.Append($"[{i}:a:0]{upmix}aresample={sampleRate},aformat=sample_fmts=fltp:channel_layouts={layout}[a{i}];");
        }
        for (var i = 0; i < inputs.Count; i++) graph.Append($"[a{i}]");
        graph.Append($"concat=n={inputs.Count}:v=0:a=1[out]");

        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[out]", "-map_metadata", "0"]);
        args.AddRange(encoding.Args);
        args.Add(output);
        return new BuiltCommand(args, encoding.Warnings);
    }

    /// <summary>Extension for a merged file: the common extension of the inputs, otherwise M4A (AAC plays everywhere).</summary>
    public static string MergeExtension(IReadOnlyList<string> inputExtensions, string choice)
    {
        if (choice != "auto") return choice;
        var distinct = inputExtensions.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : "m4a";
    }
}
