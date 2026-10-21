namespace DropFlow.Engines.Media.Encoding;

/// <summary>A named, ready-made <see cref="EncoderSettings"/> shown in the Encoder window.</summary>
/// <param name="Id">Stable identifier (also the resource key suffix <c>encoder.preset.{Id}</c>).</param>
public sealed record EncoderPreset(string Id, string DisplayName, string Description, EncoderSettings Settings);

/// <summary>
/// Built-in encoder presets. Size-limited presets use <see cref="RateControl.TargetSize"/> with 1 MB = 1,000,000 bytes, so the
/// result stays under the platform's limit whether the platform counts MB or MiB. Hardware is <see cref="HardwarePreference.Auto"/>
/// for the quality presets and <see cref="HardwarePreference.None"/> for size-limited ones (two-pass software encoding hits the size most precisely).
/// </summary>
public static class EncoderPresets
{
    public static EncoderSettings SameQualitySmaller { get; } = new()
    {
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodec.H265,
        Crf = 26,
        Preset = "slow",
        AudioCodec = AudioCodec.Aac,
        AudioBitrateKbps = 128,
        Hardware = HardwarePreference.None, // hardware HEVC is larger at the same quality; this preset exists to save space
    };

    /// <summary>WhatsApp video message: 16 MB limit, 720p, 30 fps, H.264 + AAC.</summary>
    public static EncoderSettings WhatsApp { get; } = new()
    {
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodec.H264,
        RateControl = RateControl.TargetSize,
        TargetSizeMb = 16,
        MaxHeight = 720,
        MaxFps = 30,
        AudioBitrateKbps = 96,
        Preset = "slow",
        Hardware = HardwarePreference.None,
    };

    /// <summary>Discord free upload: 10 MB.</summary>
    public static EncoderSettings Discord10MB { get; } = new()
    {
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodec.H264,
        RateControl = RateControl.TargetSize,
        TargetSizeMb = 10,
        MaxFps = 30,
        AudioBitrateKbps = 96,
        Preset = "slow",
        Hardware = HardwarePreference.None,
    };

    /// <summary>Typical email attachment limit: 25 MB.</summary>
    public static EncoderSettings Email25MB { get; } = new()
    {
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodec.H264,
        RateControl = RateControl.TargetSize,
        TargetSizeMb = 25,
        MaxHeight = 720,
        MaxFps = 30,
        AudioBitrateKbps = 128,
        Preset = "slow",
        Hardware = HardwarePreference.None,
    };

    /// <summary>YouTube upload at up to 1080p: H.264 High, near-visually-lossless CRF, AAC 192k, fast start.</summary>
    public static EncoderSettings YouTube1080p { get; } = new()
    {
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodec.H264,
        Crf = 18,
        Preset = "slow",
        MaxHeight = 1080,
        AudioBitrateKbps = 192,
        Hardware = HardwarePreference.None,
    };

    /// <summary>YouTube upload up to 2160p (4K).</summary>
    public static EncoderSettings YouTube4K { get; } = new()
    {
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodec.H264,
        Crf = 18,
        Preset = "medium",
        MaxHeight = 2160,
        AudioBitrateKbps = 256,
        Hardware = HardwarePreference.Auto,
    };

    /// <summary>Instagram Reels / TikTok / Shorts: 1080x1920, 30 fps. <paramref name="mode"/> picks letterbox (keep everything) or crop (fill the screen).</summary>
    public static EncoderSettings Reels9x16(FitMode mode = FitMode.Crop) => new()
    {
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodec.H264,
        Crf = 20,
        Preset = "medium",
        Canvas = new CanvasSize(1080, 1920, mode),
        MaxFps = 30,
        AudioBitrateKbps = 128,
        Hardware = HardwarePreference.None,
    };

    /// <summary>Looping animated GIF: 480 px wide, 15 fps, optimised palette, no audio.</summary>
    public static EncoderSettings GifLoop { get; } = new()
    {
        Container = ContainerFormat.Gif,
        MaxWidth = 480,
        MaxFps = 15,
    };

    public static EncoderSettings AudioMp3_192k { get; } = new()
    {
        ExtractAudioOnly = true,
        AudioCodec = AudioCodec.Mp3,
        AudioBitrateKbps = 192,
    };

    public static EncoderSettings AudioFlacLossless { get; } = new()
    {
        ExtractAudioOnly = true,
        AudioCodec = AudioCodec.Flac,
    };

    public static EncoderSettings AudioAacM4a_192k { get; } = new()
    {
        ExtractAudioOnly = true,
        AudioCodec = AudioCodec.Aac,
        AudioBitrateKbps = 192,
    };

    public static EncoderSettings AudioOpus_96k { get; } = new()
    {
        ExtractAudioOnly = true,
        AudioCodec = AudioCodec.Opus,
        AudioBitrateKbps = 96,
    };

    /// <summary>All presets in display order.</summary>
    public static IReadOnlyList<EncoderPreset> All { get; } =
    [
        new("same-quality-smaller", "Same quality, smaller", "H.265 at near-original quality; typically 30-50% smaller than H.264.", SameQualitySmaller),
        new("whatsapp", "WhatsApp", "Under 16 MB, 720p, 30 fps.", WhatsApp),
        new("discord-10mb", "Discord 10 MB", "Fits Discord's free upload limit.", Discord10MB),
        new("email-25mb", "Email 25 MB", "Fits a typical mail attachment limit, 720p.", Email25MB),
        new("youtube-1080p", "YouTube 1080p", "H.264 High, CRF 18, AAC 192k.", YouTube1080p),
        new("youtube-4k", "YouTube 4K", "H.264, CRF 18, up to 2160p, AAC 256k.", YouTube4K),
        new("reels-9x16", "Reels / TikTok 9:16", "1080x1920, 30 fps, cropped to fill the screen.", Reels9x16()),
        new("gif-loop", "GIF loop", "Looping animated GIF, 480 px, 15 fps.", GifLoop),
        new("audio-mp3-192k", "MP3 192 kbps", "Sound only, MP3.", AudioMp3_192k),
        new("audio-m4a-192k", "M4A (AAC) 192 kbps", "Sound only, AAC in M4A.", AudioAacM4a_192k),
        new("audio-opus-96k", "Opus 96 kbps", "Sound only, Opus.", AudioOpus_96k),
        new("audio-flac", "FLAC lossless", "Sound only, lossless.", AudioFlacLossless),
    ];

    public static EncoderPreset? ById(string id) => All.FirstOrDefault(p => p.Id == id);
}
