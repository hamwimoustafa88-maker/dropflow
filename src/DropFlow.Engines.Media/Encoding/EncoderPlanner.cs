using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Encoding;

/// <summary>
/// Turns <see cref="EncoderSettings"/> and a probed <see cref="MediaInfo"/> into FFmpeg argument lists. Pure: no process is
/// started and no file is touched, so every rule here is unit-tested without FFmpeg.
/// <para>Rules that matter:</para>
/// <list type="bullet">
/// <item>Incompatible choices are corrected with a <see cref="EncoderPlan.Warnings"/> entry rather than failing at run time
///   (H.264 in WebM becomes VP9, Opus in MOV becomes AAC, stream copy plus a filter becomes a re-encode ...).</item>
/// <item>A missing encoder degrades along a fixed chain (H.264: libx264, libopenh264, h264_mf; H.265 falls back to H.264) and says so.</item>
/// <item>Target size = bitrate arithmetic (<see cref="BitrateMath"/>) plus two-pass encoding for software H.264/H.265/VP9, and an
///   automatic step down in resolution when the budget would otherwise leave too few bits per pixel.</item>
/// <item>Widths and heights are always even (yuv420p requires it).</item>
/// </list>
/// </summary>
public static class EncoderPlanner
{
    public static EncoderPlan Plan(EncoderSettings settings, MediaInfo info, PlanContext? context = null) =>
        new PlanBuilder(settings, info, context ?? new PlanContext()).Build();

    internal static readonly string[] PresetNames =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    /// <summary>Resolution steps tried, largest first, when a target size cannot be reached at the current size.</summary>
    internal static readonly int[] HeightLadder = [2160, 1440, 1080, 720, 540, 480, 360, 288, 240];

    internal static int DefaultCrf(VideoCodec codec) => codec switch
    {
        VideoCodec.H265 => 28,
        VideoCodec.Vp9 => 32,
        VideoCodec.Av1 => 30,
        _ => 23,
    };

    /// <summary>Minimum bits per pixel per frame before the picture is visibly poor.</summary>
    internal static double MinBitsPerPixel(VideoCodec codec, bool hardware) =>
        (codec switch { VideoCodec.H265 => 0.04, VideoCodec.Vp9 => 0.04, VideoCodec.Av1 => 0.03, _ => 0.06 }) * (hardware ? 1.3 : 1.0);

    private sealed class PlanBuilder(EncoderSettings s, MediaInfo info, PlanContext ctx)
    {
        private readonly List<string> _warnings = [];
        private readonly FfmpegEncoders _encoders = ctx.Encoders ?? FfmpegEncoders.AssumeSoftwareDefaults;

        private string Input => string.IsNullOrEmpty(info.Path) ? EncoderPlan.InputPlaceholder : info.Path;
        private string Output => ctx.OutputPath ?? EncoderPlan.OutputPlaceholder;

        private double _trimStart;
        private double? _trimEnd;
        private double? _outDuration;

        public EncoderPlan Build()
        {
            _trimStart = Math.Max(0, s.TrimStart?.TotalSeconds ?? 0);
            _trimEnd = s.TrimEnd?.TotalSeconds;
            if (_trimEnd is { } te && te <= _trimStart) return Fail("The trim end must be after the trim start.");
            var src = info.DurationSeconds;
            if (src is > 0)
            {
                if (_trimStart >= src) return Fail("The trim start is past the end of the file.");
                if (_trimEnd > src) _trimEnd = src;
            }
            _outDuration = _trimEnd is { } e ? e - _trimStart : src is > 0 ? src - _trimStart : null;

            if (s.ExtractAudioOnly) return PlanAudioOnly();
            if (!info.HasVideo) return Fail("The file has no video stream. Choose 'extract audio only' to export its sound.");
            return s.Container == ContainerFormat.Gif ? PlanGif() : PlanVideo();
        }

        private EncoderPlan Fail(string error) => EncoderPlan.Fail(error, _warnings);

        // ---------------------------------------------------------------- video

        private EncoderPlan PlanVideo()
        {
            var container = s.Container;
            var vcodec = s.VideoCodec;
            var acodec = s.AudioCodec;
            var hasAudio = info.HasAudio && !s.RemoveAudio;
            var video = info.Video!;

            // ---- stream copy feasibility
            if (vcodec == VideoCodec.Copy)
            {
                var needsFilters = s.Deinterlace || s.VideoFilters.Count > 0 || FpsFilter() is not null
                    || ResolveGeometry(null, forceEven: false).Filter is not null || s.RateControl == RateControl.TargetSize;
                if (needsFilters)
                {
                    _warnings.Add("Stream copy cannot be combined with filters, resizing or a target size; the video is re-encoded as H.264.");
                    vcodec = VideoCodec.H264;
                }
                else if (!ContainerAcceptsVideo(container, video.CodecName))
                {
                    _warnings.Add($"The source video ({video.CodecName}) cannot be stored in {container.ToString().ToUpperInvariant()}; it is re-encoded as H.264.");
                    vcodec = VideoCodec.H264;
                }
            }

            // ---- container / codec compatibility
            if (container == ContainerFormat.Webm && vcodec is VideoCodec.H264 or VideoCodec.H265)
            {
                _warnings.Add("WebM cannot hold H.264/H.265; VP9 is used instead.");
                vcodec = VideoCodec.Vp9;
            }
            if (container == ContainerFormat.Mov && vcodec is VideoCodec.Av1 or VideoCodec.Vp9)
            {
                _warnings.Add("MOV cannot hold AV1/VP9; H.264 is used instead.");
                vcodec = VideoCodec.H264;
            }

            // ---- audio decisions
            var audioFilters = s.AudioFilters;
            if (hasAudio)
            {
                if (acodec == AudioCodec.Copy && audioFilters.Count > 0)
                {
                    _warnings.Add("Audio filters need re-encoding; the audio is encoded as AAC.");
                    acodec = AudioCodec.Aac;
                }
                if (acodec == AudioCodec.Copy && !ContainerAcceptsAudio(container, info.Audio!.CodecName))
                {
                    acodec = container == ContainerFormat.Webm ? AudioCodec.Opus : AudioCodec.Aac;
                    _warnings.Add($"The source audio ({info.Audio.CodecName}) cannot be stored in {container.ToString().ToUpperInvariant()}; it is re-encoded.");
                }
                if (container == ContainerFormat.Webm && acodec is AudioCodec.Aac or AudioCodec.Mp3 or AudioCodec.Flac)
                {
                    _warnings.Add("WebM needs Opus audio; Opus is used instead.");
                    acodec = AudioCodec.Opus;
                }
                if (container == ContainerFormat.Mov && acodec is AudioCodec.Opus or AudioCodec.Flac)
                {
                    _warnings.Add("MOV works best with AAC audio; AAC is used instead.");
                    acodec = AudioCodec.Aac;
                }
                if (container == ContainerFormat.Mp4 && acodec == AudioCodec.Flac)
                {
                    _warnings.Add("FLAC in MP4 is poorly supported; AAC is used instead.");
                    acodec = AudioCodec.Aac;
                }
            }

            if (video.IsHdr && vcodec != VideoCodec.Copy)
                _warnings.Add("The source is HDR; no tone mapping is applied, so colors may look washed out in the SDR result.");

            // ---- video encoder
            string? vEncoder = null;
            HardwareEncoder? hw = null;
            if (vcodec != VideoCodec.Copy)
            {
                var resolved = ResolveVideoEncoder(vcodec);
                if (resolved.Error is not null) return Fail(resolved.Error);
                vcodec = resolved.Codec;
                vEncoder = resolved.Encoder;
                hw = resolved.Hardware;
            }
            // ---- rate control + geometry
            var crf = Math.Clamp(s.Crf ?? DefaultCrf(vcodec), 0, vcodec is VideoCodec.Vp9 or VideoCodec.Av1 ? 63 : 51);
            int? bitrate = null;
            int? maxHeightOverride = null;
            TargetSizePlan? target = null;

            if (vcodec != VideoCodec.Copy)
            {
                switch (s.RateControl)
                {
                    case RateControl.Bitrate:
                        if (s.VideoBitrateKbps is not > 0) return Fail("Set a video bitrate in kbps for bitrate mode.");
                        bitrate = s.VideoBitrateKbps;
                        break;
                    case RateControl.TargetSize:
                        if (s.TargetSizeMb is not > 0) return Fail("Set a target size in MB.");
                        if (_outDuration is not > 0) return Fail("The video length is unknown, so a target size cannot be planned.");
                        var audioKbps = !hasAudio ? 0
                            : acodec == AudioCodec.Copy ? (int)Math.Round((info.Audio?.BitRate ?? 128_000) / 1000.0)
                            : s.AudioBitrateKbps;
                        target = BitrateMath.PlanTargetSize(s.TargetSizeMb.Value, _outDuration.Value, hasAudio, audioKbps);
                        if (!target.Feasible) return Fail(target.Problem!);
                        bitrate = target.VideoKbps;
                        maxHeightOverride = ShrinkForBudget(target.VideoKbps, vcodec, hw is not null);
                        break;
                    default:
                        if (vEncoder is "libopenh264" or "h264_mf")
                        {
                            // These encoders have no constant-quality mode: estimate a bitrate from the picture size.
                            var g0 = ResolveGeometry(null, true);
                            bitrate = BitrateMath.EstimateVideoKbps(g0.Width ?? video.DisplayWidth ?? 1280, g0.Height ?? video.DisplayHeight ?? 720, video.FrameRate ?? 30);
                            _warnings.Add($"{vEncoder} has no constant-quality mode; an estimated {bitrate} kbps is used.");
                        }
                        break;
                }
            }

            var geometry = vcodec == VideoCodec.Copy ? new Geometry(null, video.DisplayWidth, video.DisplayHeight) : ResolveGeometry(maxHeightOverride, forceEven: true);
            if (maxHeightOverride is not null && geometry.Height is { } gh)
                _warnings.Add($"Scaled down to {gh}p so {s.TargetSizeMb:0.#} MB still gives a clean picture.");

            if (s.Canvas is { } cv && (cv.Width % 2 != 0 || cv.Height % 2 != 0))
                return Fail("The canvas width and height must be even numbers.");

            // ---- assemble
            var twoPass = s.RateControl == RateControl.TargetSize && s.TwoPass && hw is null
                          && vcodec is VideoCodec.H264 or VideoCodec.H265 or VideoCodec.Vp9 && vEncoder is "libx264" or "libx265" or "libvpx-vp9";

            var vf = BuildVideoFilterChain(geometry);
            string? aEncoder = null;
            int? aKbps = null;
            var audioArgs = new List<string> { "-an" };
            if (hasAudio)
            {
                audioArgs = BuildAudioArgs(acodec, s.AudioBitrateKbps, out aEncoder, out aKbps);
                if (target is not null && acodec != AudioCodec.Copy)
                {
                    aKbps = target.AudioKbps;
                    ReplaceBitrate(audioArgs, target.AudioKbps);
                }
            }

            var passes = new List<IReadOnlyList<string>>();
            var videoMap = new List<string> { "-map", MapOf(video) };

            if (twoPass)
            {
                var p1 = TrimInput(Global(noOverwrite: false));
                p1.AddRange(["-i", Input]);
                p1.AddRange(videoMap);
                p1.AddRange(TrimOutput());
                if (vf is not null) p1.AddRange(["-vf", vf]);
                p1.AddRange(BuildVideoArgs(vcodec, vEncoder!, hw, crf, bitrate, pass: 1, container));
                p1.AddRange(["-an", "-f", "null", FfmpegCommand.NullDevice]);
                passes.Add(p1);
            }

            var main = TrimInput(Global(noOverwrite: true));
            main.AddRange(["-i", Input]);
            main.AddRange(videoMap);
            if (hasAudio) main.AddRange(["-map", "0:a:0?"]);
            main.AddRange(TrimOutput());
            if (vf is not null) main.AddRange(["-vf", vf]);
            if (audioFilters.Count > 0 && hasAudio && acodec != AudioCodec.Copy) main.AddRange(["-af", string.Join(",", audioFilters)]);
            main.AddRange(vcodec == VideoCodec.Copy
                ? BuildCopyVideoArgs(container, video)
                : BuildVideoArgs(vcodec, vEncoder!, hw, crf, bitrate, twoPass ? 2 : null, container));
            main.AddRange(audioArgs);
            main.AddRange(ContainerArgs(container));
            main.AddRange(MetadataArgs());
            main.Add(Output);
            passes.Add(main);

            return new EncoderPlan
            {
                Passes = passes,
                OutputExtension = ExtensionOf(container),
                Warnings = _warnings,
                VideoEncoder = vcodec == VideoCodec.Copy ? "copy" : vEncoder,
                AudioEncoder = hasAudio ? aEncoder : null,
                VideoBitrateKbps = target?.VideoKbps ?? bitrate,
                AudioBitrateKbps = hasAudio ? aKbps : null,
                OutputDurationSeconds = _outDuration,
                OutputWidth = geometry.Width,
                OutputHeight = geometry.Height,
            };
        }

        /// <summary>When the video budget gives too few bits per pixel, returns the next smaller height; otherwise null.</summary>
        private int? ShrinkForBudget(int videoKbps, VideoCodec codec, bool hardware)
        {
            if (!s.AllowDownscaleForTargetSize || s.Canvas is not null || s.VideoFilters.Count > 0) return null;
            var v = info.Video!;
            var geom = ResolveGeometry(null, forceEven: true);
            var w = geom.Width ?? v.DisplayWidth;
            var h = geom.Height ?? v.DisplayHeight;
            if (w is not > 0 || h is not > 0) return null;

            var fps = Math.Min(v.FrameRate ?? 30, s.MaxFps ?? double.MaxValue);
            var min = MinBitsPerPixel(codec, hardware);
            int? chosen = null;
            var curW = w.Value;
            var curH = h.Value;
            while (BitrateMath.BitsPerPixel(videoKbps, curW, curH, fps) < min)
            {
                var next = HeightLadder.FirstOrDefault(step => step < curH);
                if (next < 240 || next == 0) break;
                chosen = next;
                curW = Even((int)Math.Round(curW * next / (double)curH));
                curH = next;
            }
            return chosen;
        }

        // ---------------------------------------------------------------- gif

        private EncoderPlan PlanGif()
        {
            if (s.RateControl != RateControl.Crf)
                _warnings.Add("GIF ignores bitrate and target-size settings; the palette decides the size. Lower the width or frame rate to shrink it.");

            var video = info.Video!;
            var srcFps = video.FrameRate;
            var fps = Math.Min(s.MaxFps ?? 15, srcFps ?? double.MaxValue);
            var maxW = s.Canvas?.Width ?? s.MaxWidth ?? 480;

            var chain = new List<string>();
            if (s.Deinterlace) chain.Add("yadif");
            chain.AddRange(s.VideoFilters);
            chain.Add($"fps={TimeSpec.Num(fps, 3)}");
            var w = video.DisplayWidth;
            if (s.VideoFilters.Count == 0 && w is > 0)
            {
                if (w > maxW) chain.Add($"scale={maxW}:-1:flags=lanczos");
            }
            else
            {
                chain.Add($"scale={FilterEscaping.Expression($"min({maxW},iw)")}:-1:flags=lanczos");
            }
            var graph = string.Join(",", chain)
                        + ",split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

            var args = TrimInput(Global(noOverwrite: true));
            args.AddRange(["-i", Input, "-map", MapOf(video)]);
            args.AddRange(TrimOutput());
            args.AddRange(["-an", "-vf", graph, "-loop", "0", Output]);

            return new EncoderPlan
            {
                Passes = [args],
                OutputExtension = "gif",
                Warnings = _warnings,
                VideoEncoder = "gif",
                OutputDurationSeconds = _outDuration,
            };
        }

        // ---------------------------------------------------------------- audio only

        private EncoderPlan PlanAudioOnly()
        {
            if (!info.HasAudio) return Fail("The file has no audio stream.");
            var acodec = s.AudioCodec;
            var audioFilters = s.AudioFilters;
            if (acodec == AudioCodec.Copy && audioFilters.Count > 0)
            {
                _warnings.Add("Audio filters need re-encoding; AAC is used.");
                acodec = AudioCodec.Aac;
            }

            var audioArgs = BuildAudioArgs(acodec, s.AudioBitrateKbps, out var aEncoder, out var aKbps);
            // Encoder fallbacks inside BuildAudioArgs may have changed the codec; the extension follows the encoder actually used.
            var ext = acodec == AudioCodec.Copy ? CopyAudioExtension(info.Audio!.CodecName) : AudioExtension(aEncoder);

            var args = TrimInput(Global(noOverwrite: true));
            args.AddRange(["-i", Input, "-vn", "-map", "0:a:0"]);
            args.AddRange(TrimOutput());
            if (audioFilters.Count > 0 && acodec != AudioCodec.Copy) args.AddRange(["-af", string.Join(",", audioFilters)]);
            args.AddRange(audioArgs);
            if (ext == "mp3") args.AddRange(["-id3v2_version", "3"]);
            args.AddRange(MetadataArgs());
            args.Add(Output);

            return new EncoderPlan
            {
                Passes = [args],
                OutputExtension = ext,
                Warnings = _warnings,
                AudioEncoder = aEncoder,
                AudioBitrateKbps = aKbps,
                OutputDurationSeconds = _outDuration,
            };
        }

        // ---------------------------------------------------------------- pieces

        private List<string> Global(bool noOverwrite) => FfmpegCommand.Global(noOverwrite);

        /// <summary>Maps the probed video stream by index, or the first video stream when the index is unknown (no ffprobe).</summary>
        private static string MapOf(StreamInfo video) => video.Index >= 0 ? $"0:{video.Index}" : "0:v:0";

        private List<string> TrimInput(List<string> args)
        {
            if (_trimStart > 0) args.AddRange(["-ss", TimeSpec.Format(_trimStart)]);
            return args;
        }

        private List<string> TrimOutput() =>
            _trimEnd is { } e ? ["-t", TimeSpec.Format(e - _trimStart)] : [];

        private List<string> MetadataArgs() =>
            s.KeepMetadata ? ["-map_metadata", "0", "-map_chapters", "0"] : ["-map_metadata", "-1", "-map_chapters", "-1"];

        private List<string> ContainerArgs(ContainerFormat container) =>
            container is ContainerFormat.Mp4 or ContainerFormat.Mov && s.FastStart ? ["-movflags", "+faststart"] : [];

        private string? FpsFilter()
        {
            if (s.MaxFps is not > 0) return null;
            var src = info.Video?.FrameRate;
            return src is null || src > s.MaxFps.Value * 1.01 ? $"fps={TimeSpec.Num(s.MaxFps.Value, 3)}" : null;
        }

        private string? BuildVideoFilterChain(Geometry geometry)
        {
            var chain = new List<string>();
            if (s.Deinterlace) chain.Add("yadif");
            chain.AddRange(s.VideoFilters);
            if (geometry.Filter is not null) chain.Add(geometry.Filter);
            if (FpsFilter() is { } fps) chain.Add(fps);
            return chain.Count == 0 ? null : string.Join(",", chain);
        }

        // ---------------------------------------------------------------- geometry

        private sealed record Geometry(string? Filter, int? Width, int? Height);

        private Geometry ResolveGeometry(int? maxHeightOverride, bool forceEven)
        {
            var v = info.Video!;
            var w = v.DisplayWidth;
            var h = v.DisplayHeight;

            if (s.Canvas is { } c)
            {
                var filter = c.Mode switch
                {
                    FitMode.Crop => $"scale={c.Width}:{c.Height}:force_original_aspect_ratio=increase:flags=lanczos,crop={c.Width}:{c.Height},setsar=1",
                    FitMode.Stretch => $"scale={c.Width}:{c.Height}:flags=lanczos,setsar=1",
                    _ => $"scale={c.Width}:{c.Height}:force_original_aspect_ratio=decrease:flags=lanczos,pad={c.Width}:{c.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1",
                };
                return new Geometry(filter, c.Width, c.Height);
            }

            var maxW = s.MaxWidth is > 0 ? Even(s.MaxWidth.Value) : (int?)null;
            var maxH = s.MaxHeight is > 0 ? Even(s.MaxHeight.Value) : (int?)null;
            if (maxHeightOverride is > 0) maxH = maxH is null ? maxHeightOverride : Math.Min(maxH.Value, maxHeightOverride.Value);

            var known = w is > 0 && h is > 0 && s.VideoFilters.Count == 0;
            if (!known)
            {
                // Custom filters (crop ...) change the frame size in ways the planner cannot see: let FFmpeg evaluate limits per frame.
                if (maxW is null && maxH is null)
                    return new Geometry(forceEven ? "scale=trunc(iw/2)*2:trunc(ih/2)*2" : null, null, null);
                if (maxW is null)
                    return new Geometry($"scale=-2:{FilterEscaping.Expression($"min({maxH},ih)")}:flags=lanczos", null, null);
                if (maxH is null)
                    return new Geometry($"scale={FilterEscaping.Expression($"min({maxW},iw)")}:-2:flags=lanczos", null, null);
                return new Geometry(
                    $"scale={FilterEscaping.Expression($"min({maxW},iw)")}:{FilterEscaping.Expression($"min({maxH},ih)")}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos",
                    null, null);
            }

            var iw = w!.Value;
            var ih = h!.Value;
            double factor = 1;
            if (maxW is { } mw && iw > mw) factor = Math.Min(factor, mw / (double)iw);
            if (maxH is { } mh && ih > mh) factor = Math.Min(factor, mh / (double)ih);

            if (factor < 1)
            {
                var heightBinds = maxH is { } mh2 && ih > mh2 && mh2 / (double)ih <= factor + 1e-9;
                if (heightBinds)
                {
                    var th = maxH!.Value;
                    return new Geometry($"scale=-2:{th}:flags=lanczos", Even((int)Math.Round(iw * th / (double)ih)), th);
                }
                var tw = maxW!.Value;
                return new Geometry($"scale={tw}:-2:flags=lanczos", tw, Even((int)Math.Round(ih * tw / (double)iw)));
            }

            if (forceEven && (iw % 2 != 0 || ih % 2 != 0))
                return new Geometry("scale=trunc(iw/2)*2:trunc(ih/2)*2", Even(iw), Even(ih));
            return new Geometry(null, iw, ih);
        }

        private static int Even(int value) => Math.Max(2, value - value % 2);

        // ---------------------------------------------------------------- encoder resolution

        private sealed record ResolvedVideo(VideoCodec Codec, string Encoder, HardwareEncoder? Hardware, string? Error);

        private ResolvedVideo ResolveVideoEncoder(VideoCodec codec)
        {
            // Hardware first (never for VP9), according to the preference and the documented fallback order.
            var caps = ctx.Hardware ?? HardwareCapabilities.None;
            var hw = HardwareEncoders.Choose(codec, s.Hardware, caps, ctx.HardwareWorks);
            if (hw is not null) return new ResolvedVideo(codec, hw.Name, hw, null);
            if (s.Hardware is HardwarePreference.Nvenc or HardwarePreference.Qsv or HardwarePreference.Amf)
                _warnings.Add($"The requested {s.Hardware} hardware encoder is not available for {codec}; software encoding is used.");

            switch (codec)
            {
                case VideoCodec.H264:
                    foreach (var name in new[] { "libx264", "libopenh264", "h264_mf" })
                        if (_encoders.Has(name))
                        {
                            if (name != "libx264") _warnings.Add($"libx264 is missing from this FFmpeg build; {name} is used (lower quality per bit).");
                            return new ResolvedVideo(codec, name, null, null);
                        }
                    return new ResolvedVideo(codec, "", null, "This FFmpeg build has no H.264 encoder.");

                case VideoCodec.H265:
                    if (_encoders.Has("libx265")) return new ResolvedVideo(codec, "libx265", null, null);
                    _warnings.Add("libx265 is missing from this FFmpeg build; H.264 is used instead.");
                    return ResolveVideoEncoder(VideoCodec.H264);

                case VideoCodec.Vp9:
                    return _encoders.Has("libvpx-vp9")
                        ? new ResolvedVideo(codec, "libvpx-vp9", null, null)
                        : new ResolvedVideo(codec, "", null, "This FFmpeg build has no VP9 encoder (libvpx-vp9).");

                case VideoCodec.Av1:
                    foreach (var name in new[] { "libsvtav1", "libaom-av1" })
                        if (_encoders.Has(name))
                        {
                            if (name != "libsvtav1") _warnings.Add("libsvtav1 is missing; the much slower libaom-av1 is used.");
                            return new ResolvedVideo(codec, name, null, null);
                        }
                    return new ResolvedVideo(codec, "", null, "This FFmpeg build has no AV1 encoder (libsvtav1 or libaom-av1).");

                default:
                    return new ResolvedVideo(codec, "", null, "Unsupported video codec.");
            }
        }

        private List<string> BuildCopyVideoArgs(ContainerFormat container, StreamInfo video)
        {
            var args = new List<string> { "-c:v", "copy" };
            if (container is ContainerFormat.Mp4 or ContainerFormat.Mov && video.CodecName == "hevc") args.AddRange(["-tag:v", "hvc1"]);
            if (_trimStart > 0 || _trimEnd is not null) args.AddRange(["-avoid_negative_ts", "make_zero"]);
            return args;
        }

        private List<string> BuildVideoArgs(VideoCodec codec, string encoder, HardwareEncoder? hw, int crf, int? bitrate, int? pass, ContainerFormat container)
        {
            var preset = PresetIndex(s.Preset);
            var args = new List<string> { "-c:v", encoder };

            void Bitrate(double maxrateFactor = 1.5, double bufFactor = 2)
            {
                var b = bitrate ?? 2000;
                args.AddRange(["-b:v", $"{b}k", "-maxrate", $"{(int)Math.Round(b * maxrateFactor)}k", "-bufsize", $"{(int)Math.Round(b * bufFactor)}k"]);
            }

            if (hw is not null)
            {
                switch (hw.Kind)
                {
                    case HardwareKind.Nvenc:
                        args.AddRange(["-preset", $"p{new[] { 1, 2, 3, 3, 4, 5, 6, 7, 7 }[preset]}", "-rc", "vbr"]);
                        if (bitrate is null) args.AddRange(["-cq", crf.ToString(), "-b:v", "0"]); else Bitrate(1.2);
                        break;
                    case HardwareKind.Qsv:
                        args.AddRange(["-preset", preset < 2 ? "veryfast" : PresetNames[preset]]);
                        if (bitrate is null) args.AddRange(["-global_quality", crf.ToString()]); else Bitrate(1.2);
                        break;
                    default: // AMF
                        args.AddRange(["-quality", preset <= 4 ? "speed" : preset == 5 ? "balanced" : "quality"]);
                        if (bitrate is null) args.AddRange(["-rc", "cqp", "-qp_i", crf.ToString(), "-qp_p", crf.ToString(), "-qp_b", crf.ToString()]);
                        else { args.AddRange(["-rc", "vbr_peak"]); Bitrate(1.2); }
                        break;
                }
                if (codec == VideoCodec.H265 && container is ContainerFormat.Mp4 or ContainerFormat.Mov) args.AddRange(["-tag:v", "hvc1"]);
                return args;
            }

            switch (encoder)
            {
                case "libx264":
                    args.AddRange(["-preset", PresetNames[preset]]);
                    if (bitrate is null) args.AddRange(["-crf", crf.ToString()]); else args.AddRange(["-b:v", $"{bitrate}k"]);
                    args.AddRange(["-pix_fmt", "yuv420p"]);
                    AddPass(args, pass);
                    break;

                case "libx265":
                    args.AddRange(["-preset", PresetNames[preset]]);
                    if (bitrate is null) args.AddRange(["-crf", crf.ToString()]); else args.AddRange(["-b:v", $"{bitrate}k"]);
                    args.AddRange(["-pix_fmt", "yuv420p"]);
                    var x265 = "log-level=error";
                    if (pass is 1 or 2) x265 += $":pass={pass}:stats={ctx.PassLogName}.log";
                    args.AddRange(["-x265-params", x265]);
                    if (container is ContainerFormat.Mp4 or ContainerFormat.Mov) args.AddRange(["-tag:v", "hvc1"]);
                    break;

                case "libvpx-vp9":
                    if (bitrate is null) args.AddRange(["-crf", crf.ToString(), "-b:v", "0"]); else args.AddRange(["-b:v", $"{bitrate}k"]);
                    args.AddRange(["-deadline", "good", "-cpu-used", new[] { 5, 5, 4, 4, 3, 2, 1, 0, 0 }[preset].ToString(), "-row-mt", "1", "-pix_fmt", "yuv420p"]);
                    AddPass(args, pass);
                    break;

                case "libsvtav1":
                    args.AddRange(["-preset", new[] { 12, 11, 10, 9, 8, 7, 5, 4, 3 }[preset].ToString()]);
                    if (bitrate is null) args.AddRange(["-crf", crf.ToString()]); else args.AddRange(["-b:v", $"{bitrate}k"]);
                    args.AddRange(["-pix_fmt", "yuv420p"]);
                    break;

                case "libaom-av1":
                    if (bitrate is null) args.AddRange(["-crf", crf.ToString(), "-b:v", "0"]); else args.AddRange(["-b:v", $"{bitrate}k"]);
                    args.AddRange(["-cpu-used", new[] { 8, 8, 7, 6, 5, 4, 3, 2, 1 }[preset].ToString(), "-row-mt", "1", "-pix_fmt", "yuv420p"]);
                    break;

                default: // libopenh264, h264_mf: bitrate only
                    args.AddRange(["-b:v", $"{bitrate ?? 2000}k", "-pix_fmt", "yuv420p"]);
                    break;
            }
            return args;
        }

        private void AddPass(List<string> args, int? pass)
        {
            if (pass is 1 or 2) args.AddRange(["-pass", pass.Value.ToString(), "-passlogfile", ctx.PassLogName]);
        }

        private static int PresetIndex(string preset)
        {
            var i = Array.IndexOf(PresetNames, (preset ?? "").Trim().ToLowerInvariant());
            return i < 0 ? 5 : i;
        }

        // ---------------------------------------------------------------- audio

        private List<string> BuildAudioArgs(AudioCodec codec, int bitrateKbps, out string? encoder, out int? kbps)
        {
            var args = new List<string>();
            kbps = null;
            encoder = null;
            var channels = s.AudioChannels;
            var srcChannels = info.Audio?.Channels;

            switch (codec)
            {
                case AudioCodec.Copy:
                    encoder = "copy";
                    return ["-c:a", "copy"];

                case AudioCodec.Flac:
                    encoder = "flac";
                    args.AddRange(["-c:a", "flac", "-compression_level", "5"]);
                    break;

                case AudioCodec.Mp3:
                    if (_encoders.Has("libmp3lame"))
                    {
                        encoder = "libmp3lame";
                        kbps = ClampKbps(bitrateKbps, 320);
                        args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{kbps}k"]);
                        if (channels is null && srcChannels > 2) channels = 2; // MP3 holds at most two channels
                        break;
                    }
                    _warnings.Add("libmp3lame is missing from this FFmpeg build; AAC is used instead of MP3.");
                    goto default;

                case AudioCodec.Opus:
                    if (_encoders.Has("libopus"))
                    {
                        encoder = "libopus";
                        kbps = ClampKbps(bitrateKbps, 510);
                        args.AddRange(["-c:a", "libopus", "-b:a", $"{kbps}k", "-vbr", "on"]);
                        break;
                    }
                    if (_encoders.Has("opus"))
                    {
                        encoder = "opus";
                        kbps = ClampKbps(bitrateKbps, 510);
                        args.AddRange(["-c:a", "opus", "-strict", "-2", "-b:a", $"{kbps}k"]);
                        _warnings.Add("libopus is missing; FFmpeg's experimental native Opus encoder is used.");
                        break;
                    }
                    _warnings.Add("No Opus encoder in this FFmpeg build; AAC is used instead.");
                    goto default;

                default: // Aac
                    encoder = "aac";
                    kbps = ClampKbps(bitrateKbps, 512);
                    args.AddRange(["-c:a", "aac", "-b:a", $"{kbps}k"]);
                    break;
            }

            if (channels is 1 or 2) args.AddRange(["-ac", channels.Value.ToString()]);
            return args;
        }

        private static int ClampKbps(int kbps, int max) => Math.Clamp(kbps <= 0 ? 128 : kbps, 16, max);

        private static void ReplaceBitrate(List<string> audioArgs, int kbps)
        {
            var i = audioArgs.IndexOf("-b:a");
            if (i >= 0 && i + 1 < audioArgs.Count) audioArgs[i + 1] = $"{kbps}k";
        }

        // ---------------------------------------------------------------- compatibility tables

        private static bool ContainerAcceptsVideo(ContainerFormat c, string? codec) => codec is null || c switch
        {
            ContainerFormat.Mkv => true,
            ContainerFormat.Mp4 => codec is "h264" or "hevc" or "av1" or "vp9" or "mpeg4",
            ContainerFormat.Mov => codec is "h264" or "hevc" or "mpeg4" or "prores",
            ContainerFormat.Webm => codec is "vp8" or "vp9" or "av1",
            _ => false,
        };

        private static bool ContainerAcceptsAudio(ContainerFormat c, string? codec) => codec is null || c switch
        {
            ContainerFormat.Mkv => true,
            ContainerFormat.Mp4 => codec is "aac" or "mp3" or "ac3" or "eac3" or "opus" or "alac",
            ContainerFormat.Mov => codec is "aac" or "mp3" or "ac3" or "alac" || codec.StartsWith("pcm_", StringComparison.Ordinal),
            ContainerFormat.Webm => codec is "opus" or "vorbis",
            _ => false,
        };

        internal static string ExtensionOf(ContainerFormat c) => c switch
        {
            ContainerFormat.Mkv => "mkv",
            ContainerFormat.Webm => "webm",
            ContainerFormat.Mov => "mov",
            ContainerFormat.Gif => "gif",
            _ => "mp4",
        };

        private static string AudioExtension(string? encoder) => encoder switch
        {
            "libmp3lame" => "mp3",
            "libopus" or "opus" => "opus",
            "flac" => "flac",
            _ => "m4a",
        };

        private static string CopyAudioExtension(string? codec) => codec switch
        {
            "aac" or "alac" => "m4a",
            "mp3" => "mp3",
            "opus" => "opus",
            "vorbis" => "ogg",
            "flac" => "flac",
            "ac3" => "ac3",
            not null when codec.StartsWith("pcm_", StringComparison.Ordinal) => "wav",
            _ => "mka",
        };
    }
}
