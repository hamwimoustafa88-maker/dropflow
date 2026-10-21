using System.Text.RegularExpressions;

namespace DropFlow.Engines.Media.Encoding;

/// <summary>The encoders an FFmpeg build contains, parsed from the output of <c>ffmpeg -hide_banner -encoders</c>.</summary>
public sealed partial class FfmpegEncoders
{
    private readonly HashSet<string> _names;

    private FfmpegEncoders(HashSet<string> names) => _names = names;

    public IReadOnlyCollection<string> Names => _names;

    public bool Has(string encoder) => _names.Contains(encoder);

    /// <summary>
    /// Parses lines such as <c> V....D libx264              libx264 H.264 / AVC ...</c>. The header block (flag legend and the
    /// dashed separator) is skipped. Unknown or empty input yields an empty set.
    /// </summary>
    public static FfmpegEncoders Parse(string encodersOutput)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var inList = false;
        foreach (var raw in encodersOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inList)
            {
                if (line.TrimStart().StartsWith("------", StringComparison.Ordinal)) inList = true;
                continue;
            }
            var m = EncoderLine().Match(line);
            if (m.Success) names.Add(m.Groups["name"].Value);
        }
        return new FfmpegEncoders(names);
    }

    /// <summary>
    /// Assumes a standard GPL build with the software encoders DropFlow prefers and no hardware encoders. Used when the real list
    /// could not be read, so a planner never fails only because probing failed.
    /// </summary>
    public static FfmpegEncoders AssumeSoftwareDefaults { get; } = new(
        ["libx264", "libx265", "libvpx-vp9", "libsvtav1", "libmp3lame", "libopus", "libvorbis", "aac", "flac", "wmav2", "ac3",
         "pcm_s16le", "pcm_s16be", "mpeg4", "wmv2", "gif"]);

    // flags (6 chars: V/A/S, F, S, X, B, D or dots) then the encoder name
    [GeneratedRegex(@"^\s*[VAS][F.][S.][X.][B.][D.]\s+(?<name>\S+)\s")]
    private static partial Regex EncoderLine();
}

public enum HardwareKind { Nvenc, Qsv, Amf }

/// <summary>A hardware video encoder present in the FFmpeg build (not proof that a GPU can run it; see <see cref="HardwareEncoders.TrialArguments"/>).</summary>
public sealed record HardwareEncoder(HardwareKind Kind, VideoCodec Codec, string Name);

/// <summary>Hardware encoders found in an FFmpeg build.</summary>
public sealed record HardwareCapabilities(IReadOnlyList<HardwareEncoder> Encoders)
{
    public static HardwareCapabilities None { get; } = new([]);

    public bool Any => Encoders.Count > 0;

    public HardwareEncoder? Find(HardwareKind kind, VideoCodec codec) =>
        Encoders.FirstOrDefault(e => e.Kind == kind && e.Codec == codec);

    public bool Has(HardwareKind kind, VideoCodec codec) => Find(kind, codec) is not null;

    /// <summary>
    /// The hardware encoder to use for <paramref name="codec"/>, following the documented order (NVENC, Quick Sync, AMF) or the forced
    /// vendor in <paramref name="preference"/>; null means "use the software encoder". See <see cref="HardwareEncoders"/>.
    /// </summary>
    public HardwareEncoder? Best(VideoCodec codec, HardwarePreference preference = HardwarePreference.Auto, Func<string, bool>? works = null) =>
        HardwareEncoders.Choose(codec, preference, this, works);
}

/// <summary>
/// Hardware encoder selection.
/// <para>
/// <b>Fallback order for <see cref="HardwarePreference.Auto"/>:</b> NVENC (NVIDIA), then Quick Sync (Intel), then AMF (AMD),
/// then software. NVENC first because it has the best quality-per-watt and the broadest driver support; Quick Sync second because
/// nearly every Intel CPU has it and it is more reliable than AMF across driver versions; AMF last. If the build or the GPU
/// lacks the codec (for example AV1 on an older card), the next kind is tried, and if none works the software encoder is used.
/// </para>
/// <para>
/// <b>A forced choice</b> (NVENC, QSV or AMF) never silently switches to another vendor: it uses that encoder or falls back to
/// software, so the user's explicit request is respected and the planner reports a warning.
/// </para>
/// <para>
/// An encoder being listed by <c>-encoders</c> only means the build includes it. Callers that can afford a one-frame trial
/// encode should pass a <c>works</c> predicate (see <see cref="TrialArguments"/>) so a missing GPU or a broken driver is detected.
/// </para>
/// </summary>
public static class HardwareEncoders
{
    public static IReadOnlyList<HardwareKind> AutoOrder { get; } = [HardwareKind.Nvenc, HardwareKind.Qsv, HardwareKind.Amf];

    /// <summary>Finds the hardware encoders in the text printed by <c>ffmpeg -encoders</c>.</summary>
    public static HardwareCapabilities Detect(string ffmpegEncodersOutput) => Detect(FfmpegEncoders.Parse(ffmpegEncodersOutput));

    public static HardwareCapabilities Detect(FfmpegEncoders encoders)
    {
        var found = new List<HardwareEncoder>();
        foreach (var kind in AutoOrder)
            foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1 })
            {
                var name = EncoderName(kind, codec);
                if (name is not null && encoders.Has(name)) found.Add(new HardwareEncoder(kind, codec, name));
            }
        return new HardwareCapabilities(found);
    }

    /// <summary>FFmpeg encoder name for a vendor/codec pair, or null when the vendor has none (VP9, stream copy).</summary>
    public static string? EncoderName(HardwareKind kind, VideoCodec codec)
    {
        var suffix = kind switch { HardwareKind.Nvenc => "nvenc", HardwareKind.Qsv => "qsv", HardwareKind.Amf => "amf", _ => null };
        var stem = codec switch { VideoCodec.H264 => "h264", VideoCodec.H265 => "hevc", VideoCodec.Av1 => "av1", _ => null };
        return suffix is null || stem is null ? null : $"{stem}_{suffix}";
    }

    /// <summary>
    /// Picks the hardware encoder to use, or null for software. <paramref name="works"/> (optional) vets an encoder name with a
    /// real trial encode; when null, being present in the build is taken as enough.
    /// </summary>
    public static HardwareEncoder? Choose(VideoCodec codec, HardwarePreference preference, HardwareCapabilities capabilities, Func<string, bool>? works = null)
    {
        if (preference == HardwarePreference.None || codec is VideoCodec.Copy or VideoCodec.Vp9) return null;

        IEnumerable<HardwareKind> order = preference switch
        {
            HardwarePreference.Nvenc => [HardwareKind.Nvenc],
            HardwarePreference.Qsv => [HardwareKind.Qsv],
            HardwarePreference.Amf => [HardwareKind.Amf],
            _ => AutoOrder,
        };

        foreach (var kind in order)
            if (capabilities.Find(kind, codec) is { } enc && (works?.Invoke(enc.Name) ?? true))
                return enc;
        return null;
    }

    /// <summary>
    /// A one-frame encode that succeeds only when the GPU, driver and build can really use <paramref name="encoderName"/>.
    /// Run it with ffmpeg and treat exit code 0 as "works". Uses a 256x256 frame (NVENC rejects very small sizes) and the
    /// <b>same vendor options the planner uses</b> (NVENC p-presets and <c>-cq</c>, QSV <c>-global_quality</c>, AMF <c>-qp_*</c>), so an
    /// FFmpeg that is too old for those options, or a driver that refuses them, is detected here and not half way through a job.
    /// </summary>
    public static IReadOnlyList<string> TrialArguments(string encoderName)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error",
            "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2:r=10",
            "-frames:v", "1", "-c:v", encoderName,
        };
        if (encoderName.EndsWith("_nvenc", StringComparison.Ordinal)) args.AddRange(["-preset", "p4", "-rc", "vbr", "-cq", "30", "-b:v", "0"]);
        else if (encoderName.EndsWith("_qsv", StringComparison.Ordinal)) args.AddRange(["-global_quality", "30"]);
        else if (encoderName.EndsWith("_amf", StringComparison.Ordinal)) args.AddRange(["-quality", "balanced", "-rc", "cqp", "-qp_i", "30", "-qp_p", "30", "-qp_b", "30"]);
        args.AddRange(["-f", "null", Ffmpeg.FfmpegCommand.NullDevice]);
        return args;
    }
}
