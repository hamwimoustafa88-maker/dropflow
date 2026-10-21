using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>Arguments plus anything the caller should know about how they were chosen.</summary>
internal sealed record BuiltCommand(IReadOnlyList<string> Args, IReadOnlyList<string> Warnings, string? Error = null)
{
    public bool Ok => Error is null;
    public static BuiltCommand Fail(string error) => new([], [], error);
}

/// <summary>Which codecs Matroska can take unchanged, so MKV output copies instead of re-encoding when it can.</summary>
internal static class MkvPolicy
{
    private static readonly HashSet<string> Video =
        ["h264", "hevc", "vp8", "vp9", "av1", "mpeg4", "mpeg2video", "mpeg1video", "theora", "vc1", "prores"];

    private static readonly HashSet<string> Audio =
        ["aac", "mp3", "mp2", "ac3", "eac3", "opus", "vorbis", "flac", "alac", "dts", "truehd"];

    public static bool CanCopyVideo(string? codec) => codec is not null && Video.Contains(codec);

    public static bool CanCopyAudio(string? codec) => codec is not null && (Audio.Contains(codec) || codec.StartsWith("pcm_", StringComparison.Ordinal));

    /// <summary>Subtitle codecs Matroska stores as they are (text and image based).</summary>
    public static bool CanCopySubtitle(string? codec) => codec is "subrip" or "ass" or "ssa" or "webvtt" or "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle";
}

/// <summary>Builds the commands for plain format conversions. Pure; the operations feed it probed info and the encoder list.</summary>
internal static class ConversionArgs
{
    /// <summary>
    /// MP4/MOV: H.264 + AAC (yuv420p, fast start). WebM: VP9 + Opus. MKV: copies the streams that Matroska accepts and re-encodes
    /// the rest as H.264/AAC. GIF: palette-optimised loop. Defaults trade a little speed for reliability, not hardware tricks:
    /// software encoders only.
    /// </summary>
    public static EncoderSettings ModernVideoSettings(string outputExtension, MediaInfo info, int? crf = null, string? preset = null, double? fps = null, int? width = null)
    {
        var container = outputExtension switch
        {
            "mkv" => ContainerFormat.Mkv,
            "webm" => ContainerFormat.Webm,
            "mov" => ContainerFormat.Mov,
            "gif" => ContainerFormat.Gif,
            _ => ContainerFormat.Mp4,
        };

        var settings = new EncoderSettings
        {
            Container = container,
            Hardware = HardwarePreference.None,
            Preset = preset ?? "fast",
            AudioBitrateKbps = container == ContainerFormat.Webm ? 128 : 192,
            KeepMetadata = true,
        };

        switch (container)
        {
            case ContainerFormat.Webm:
                return settings with { VideoCodec = VideoCodec.Vp9, AudioCodec = AudioCodec.Opus, Crf = crf is null ? null : Math.Min(63, crf.Value + 8) };

            case ContainerFormat.Mkv:
                return settings with
                {
                    VideoCodec = MkvPolicy.CanCopyVideo(info.Video?.CodecName) ? VideoCodec.Copy : VideoCodec.H264,
                    AudioCodec = !info.HasAudio || MkvPolicy.CanCopyAudio(info.Audio?.CodecName) ? AudioCodec.Copy : AudioCodec.Aac,
                    Crf = crf ?? 21,
                };

            case ContainerFormat.Gif:
                return settings with { MaxFps = fps ?? 15, MaxWidth = width ?? 480 };

            default:
                return settings with { VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.Aac, Crf = crf ?? 21 };
        }
    }

    /// <summary>
    /// Any audio-bearing file to an audio format: first audio stream only, tags kept. Cover art is carried over (as an attached
    /// picture) when both formats can hold it; the caller retries without it if FFmpeg refuses.
    /// </summary>
    public static BuiltCommand Audio(string input, string output, string outputExtension, MediaInfo? source, FfmpegEncoders encoders, int? bitrateKbps, bool includeCoverArt, AudioQuality quality = AudioQuality.Standard, string? audioFilter = null, int? sampleRate = null)
    {
        var encoding = AudioFormats.Encode(outputExtension, bitrateKbps, quality, source, encoders);
        if (!encoding.Ok) return BuiltCommand.Fail(encoding.Error!);

        var args = FfmpegCommand.Global();
        args.AddRange(["-i", input, "-map", "0:a:0"]);
        var cover = source?.CoverArt;
        var withCover = includeCoverArt && cover is not null && AudioFormats.CarriesCoverArt(outputExtension);
        if (withCover) args.AddRange(["-map", $"0:{cover!.Index}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        if (audioFilter is not null) args.AddRange(["-af", audioFilter]);
        args.AddRange(["-map_metadata", "0"]);
        args.AddRange(encoding.Args);
        // -ar rather than an aresample filter: the output option lets FFmpeg negotiate the channel layout itself. A filter placed after
        // loudnorm fails on files with a guessed layout ("Cannot select channel layout for the link between filters").
        if (sampleRate is > 0) args.AddRange(["-ar", sampleRate.Value.ToString()]);
        args.Add(output);
        return new BuiltCommand(args, encoding.Warnings);
    }

    /// <summary>
    /// AVI and WMV outputs: MPEG-4 Part 2 + MP3 (AC-3 if MP3 is missing) for AVI, WMV2 + WMA for WMV. These are the codecs old
    /// players expect inside those containers; both video encoders are native to FFmpeg so they are always available.
    /// </summary>
    public static BuiltCommand LegacyVideo(string input, string output, string outputExtension, MediaInfo? source, FfmpegEncoders encoders)
    {
        var warnings = new List<string>();
        var args = FfmpegCommand.Global();
        args.AddRange(["-i", input, "-map", source?.Video is { } v ? $"0:{v.Index}" : "0:v:0"]);
        var hasAudio = source is null || source.HasAudio;
        if (hasAudio) args.AddRange(["-map", "0:a:0?"]);
        args.AddRange(["-sn", "-map_metadata", "0"]);
        args.AddRange(["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-pix_fmt", "yuv420p"]);

        var channels = source?.Audio?.Channels;
        switch (outputExtension)
        {
            case "avi":
                args.AddRange(["-c:v", "mpeg4", "-q:v", "3", "-vtag", "XVID"]);
                if (hasAudio)
                {
                    if (encoders.Has("libmp3lame"))
                    {
                        args.AddRange(["-c:a", "libmp3lame", "-q:a", "2"]);
                    }
                    else
                    {
                        warnings.Add("libmp3lame is missing; AC-3 audio is used in the AVI.");
                        args.AddRange(["-c:a", "ac3", "-b:a", "192k"]);
                    }
                    if (channels > 2) args.AddRange(["-ac", "2"]);
                }
                break;

            case "wmv":
                args.AddRange(["-c:v", "wmv2", "-q:v", "3"]);
                if (hasAudio)
                {
                    args.AddRange(["-c:a", "wmav2", "-b:a", "192k"]);
                    if (channels > 2) args.AddRange(["-ac", "2"]);
                }
                break;

            default:
                return BuiltCommand.Fail($"'.{outputExtension}' is not a legacy video output.");
        }

        args.Add(output);
        return new BuiltCommand(args, warnings);
    }
}
