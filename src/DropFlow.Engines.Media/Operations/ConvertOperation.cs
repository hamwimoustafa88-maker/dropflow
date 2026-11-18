using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Ffmpeg;
using DropFlow.Engines.Media.Runtime;

namespace DropFlow.Engines.Media.Operations;

/// <summary>
/// Format conversion for the <c>audio</c> and <c>video</c> catalog categories: <c>convert:{ext}</c>.
/// <list type="bullet">
/// <item>Audio in (mp3 m4a wav flac ogg opus aiff wma aac, and the read-only sources amr ape wv tta dsf dff ac3 dts mka caf m4r) to any
///   other audio format, including m4r (an iPhone ringtone: AAC, at most 40 seconds; a longer file is cut with a note) and ac3. Tags are
///   kept; cover art is carried to mp3/m4a/flac. DSD sources are resampled (<see cref="AudioFormats.DsdTargetRate"/>).</item>
/// <item>Video in (mp4 mov mkv webm avi wmv flv m4v ts m2ts mts 3gp ogv vob mpg mpeg dat) to mp4 mkv webm avi wmv gif mov, to looping
///   animated webp (when the FFmpeg build has libwebp) and apng, or to the audio formats mp3, m4a, wav.</item>
/// <item><b>GIF as an input:</b> the catalog files gif under <i>images</i> (the wheel shows image targets for a .gif), but an animated GIF
///   is also a perfectly good video source, so this converter supports gif to mp4/mkv/webm/mov/avi/wmv when asked for explicitly.
///   It is never offered for audio targets (a GIF has no sound).</item>
/// </list>
/// Never asks for options; sensible defaults apply. Optional keys in <c>ConversionJob.Options</c> (all unset by default, for the CLI and
/// recipes): <c>bitrate</c> (kbps, audio outputs), <c>codec</c> (<c>aac</c> or <c>alac</c>, m4a output), <c>crf</c> (video quality, 16-35),
/// <c>preset</c> (x264 speed name), <c>fps</c> and <c>width</c> (gif, webp and apng output).
/// </summary>
internal sealed class ConvertOperation : MediaOperation
{
    private static readonly string[] VideoInputs = ["mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v", "ts", "m2ts", "mts", "3gp", "ogv", "vob", "mpg", "mpeg", "dat"];
    private static readonly string[] VideoOutputs = ["mp4", "mkv", "webm", "avi", "wmv", "gif", "mov", "webp", "apng"];
    private static readonly string[] VideoToAudio = ["mp3", "m4a", "wav"];
    private static readonly string[] GifToVideo = ["mp4", "mkv", "webm", "avi", "wmv", "mov"];

    public override IReadOnlyCollection<string> Ids { get; } =
        AudioFormats.OutputExtensions.Concat(VideoOutputs).Distinct().Select(Operation.Convert).ToArray();

    public override bool Supports(string operation, string inputExtension)
    {
        if (!Operation.IsConvert(operation)) return false;
        var target = Operation.TargetOf(operation);
        if (target == inputExtension) return false;

        if (AudioFormats.IsConvertibleAudio(inputExtension)) return AudioFormats.IsOutputExtension(target);
        if (VideoInputs.Contains(inputExtension)) return VideoOutputs.Contains(target) || VideoToAudio.Contains(target);
        if (inputExtension == "gif") return GifToVideo.Contains(target);
        return false;
    }

    public override async Task<IReadOnlyList<string>> ExecuteAsync(MediaContext ctx)
    {
        var target = Operation.TargetOf(ctx.Job.Operation);
        var info = await ctx.TryProbeAsync(ctx.Input).ConfigureAwait(false);
        return AudioFormats.IsOutputExtension(target)
            ? [await ToAudioAsync(ctx, target, info).ConfigureAwait(false)]
            : [await ToVideoAsync(ctx, target, info).ConfigureAwait(false)];
    }

    private static async Task<string> ToAudioAsync(MediaContext ctx, string target, MediaInfo? info)
    {
        if (info is not null && !info.HasAudio) throw new MediaFailure("The file has no audio track to convert.");

        var encoders = await ctx.EncodersAsync().ConfigureAwait(false);
        var bitrate = ctx.Int("bitrate", 0, 0, 512);
        var alac = target == "m4a" && ctx.Choice("codec", "aac", ["aac", "alac"]) == "alac";
        int? sampleRate = AudioFormats.IsDsd(ctx.InputExtension) ? AudioFormats.DsdTargetRate(target) : null;
        int? maxSeconds = target == "m4r" ? AudioFormats.RingtoneMaxSeconds : null;
        var output = ctx.NewFile(target, "converted");

        BuiltCommand Build(bool cover) =>
            ConversionArgs.Audio(ctx.Input, output, target, info, encoders, bitrate > 0 ? bitrate : null, includeCoverArt: cover,
                sampleRate: sampleRate, maxSeconds: maxSeconds, alac: alac);

        var command = Build(cover: true);
        if (!command.Ok) throw new MediaFailure(command.Error!);
        if (command.Warnings.Count > 0) ctx.Progress.Report(new JobProgress(0, string.Join(" ", command.Warnings)));

        var expected = info?.DurationSeconds is { } d && maxSeconds is { } max ? Math.Min(d, max) : info?.DurationSeconds;
        var result = await ctx.RunFfmpegAsync(command.Args, expected).ConfigureAwait(false);
        if (!result.Success && command.Args.Contains("attached_pic"))
        {
            // Some cover-art/format pairs are rejected by the muxer; the sound matters more than the picture.
            if (File.Exists(output)) File.Delete(output);
            command = Build(cover: false);
            result = await ctx.RunFfmpegAsync(command.Args, expected).ConfigureAwait(false);
        }
        if (!result.Success) throw new MediaFailure(FfmpegErrors.Summarize(result.StdErr, result.ExitCode));

        MediaContext.RequireOutput(output);
        if (maxSeconds is { } limit && info?.DurationSeconds > limit + 0.05)
            ctx.Note = NoteText.Key("note.aud.ringtoneCut", limit);
        return output;
    }

    private static async Task<string> ToVideoAsync(MediaContext ctx, string target, MediaInfo? info)
    {
        if (info is not null && !info.HasVideo) throw new MediaFailure("The file has no video stream to convert.");
        var output = ctx.NewFile(target, "converted");

        if (target is "webp" or "apng")
        {
            var encoders = await ctx.EncodersAsync().ConfigureAwait(false);
            var fpsOption = ctx.Number("fps", 0, 0, 60);
            var widthOption = ctx.Int("width", 0, 0, 7680);
            var command = ConversionArgs.Animation(ctx.Input, output, target, info, encoders, fpsOption > 0 ? fpsOption : 15, widthOption > 0 ? widthOption : 480);
            if (!command.Ok) throw new MediaFailure(command.Error!);
            await ctx.RunFfmpegOrFailAsync(command.Args, info?.DurationSeconds).ConfigureAwait(false);
            MediaContext.RequireOutput(output);
            return output;
        }

        if (target is "avi" or "wmv")
        {
            var encoders = await ctx.EncodersAsync().ConfigureAwait(false);
            var command = ConversionArgs.LegacyVideo(ctx.Input, output, target, info, encoders);
            if (!command.Ok) throw new MediaFailure(command.Error!);
            await ctx.RunFfmpegOrFailAsync(command.Args, info?.DurationSeconds).ConfigureAwait(false);
            MediaContext.RequireOutput(output);
            return output;
        }

        var effective = info ?? MediaContextExtensions.Assumed(ctx.Input);
        var crf = ctx.Int("crf", 0, 0, 51);
        var preset = ctx.Text("preset");
        var fps = ctx.Number("fps", 0, 0, 60);
        var width = ctx.Int("width", 0, 0, 7680);
        var settings = ConversionArgs.ModernVideoSettings(
            target, effective,
            crf > 0 ? crf : null,
            preset.Length > 0 ? preset : null,
            fps > 0 ? fps : null,
            width > 0 ? width : null);

        await ctx.EncodeAsync(settings, effective, output).ConfigureAwait(false);
        return output;
    }
}

internal static class MediaContextExtensions
{
    /// <summary>
    /// Stand-in for ffprobe output when ffprobe is not installed: one video and one optional audio stream of unknown properties.
    /// Planning then falls back to FFmpeg-evaluated filter expressions and runs without a progress fraction.
    /// </summary>
    public static MediaInfo Assumed(string path) => new()
    {
        Path = path,
        Streams =
        [
            new StreamInfo { Index = -1, Kind = StreamKind.Video }, // -1: map by position (0:v:0), the real index is unknown
            new StreamInfo { Index = 1, Kind = StreamKind.Audio },
        ],
    };
}
