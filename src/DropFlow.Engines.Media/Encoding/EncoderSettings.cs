namespace DropFlow.Engines.Media.Encoding;

/// <summary>Output container. <see cref="Gif"/> is the animated-GIF "container" (palette-optimised, no audio).</summary>
public enum ContainerFormat { Mp4, Mkv, Webm, Mov, Gif }

public enum VideoCodec { H264, H265, Av1, Vp9, Copy }

public enum AudioCodec { Aac, Opus, Mp3, Flac, Copy }

/// <summary>Which encoder family to use. Auto = NVENC, then Quick Sync, then AMF, then software (see <see cref="HardwareEncoders"/>).</summary>
public enum HardwarePreference { Auto, None, Nvenc, Qsv, Amf }

public enum RateControl
{
    /// <summary>Constant quality: file size follows the content. Best default.</summary>
    Crf,
    /// <summary>Average bitrate in kbps.</summary>
    Bitrate,
    /// <summary>Hit a file size (MB): bitrate is computed from the duration and, for software encoders, encoded in two passes.</summary>
    TargetSize,
}

/// <summary>How a clip is fitted onto a fixed canvas such as 1080x1920.</summary>
public enum FitMode
{
    /// <summary>Whole picture visible, black bars fill the rest.</summary>
    Letterbox,
    /// <summary>Canvas filled completely, the overflow is cropped away.</summary>
    Crop,
    /// <summary>Stretched to the canvas (distorts the picture).</summary>
    Stretch,
}

/// <summary>An exact output frame size.</summary>
public sealed record CanvasSize(int Width, int Height, FitMode Mode = FitMode.Letterbox);

/// <summary>
/// Everything the Encoder window (and the compress / target-size tools) can ask for. Immutable: use <c>with</c> to derive variants.
/// <see cref="EncoderPlanner"/> turns it plus the probed <see cref="MediaInfo"/> into FFmpeg arguments.
/// </summary>
public sealed record EncoderSettings
{
    public ContainerFormat Container { get; init; } = ContainerFormat.Mp4;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.H264;
    public HardwarePreference Hardware { get; init; } = HardwarePreference.Auto;

    public RateControl RateControl { get; init; } = RateControl.Crf;
    /// <summary>Constant-quality value (lower = better). Null uses the codec's default: H.264 23, H.265 28, VP9 32, AV1 30.</summary>
    public int? Crf { get; init; }
    public int? VideoBitrateKbps { get; init; }
    /// <summary>For <see cref="RateControl.TargetSize"/>: wanted size in MB (1 MB = 1,000,000 bytes).</summary>
    public double? TargetSizeMb { get; init; }
    /// <summary>Two-pass encoding for target size. Honoured by software H.264, H.265 and VP9; other encoders use single-pass VBR.</summary>
    public bool TwoPass { get; init; } = true;
    /// <summary>When the target size leaves too few bits per pixel, lower the resolution instead of producing mush.</summary>
    public bool AllowDownscaleForTargetSize { get; init; } = true;

    /// <summary>x264-style speed preset (ultrafast ... veryslow); mapped to each encoder's own scale.</summary>
    public string Preset { get; init; } = "medium";

    /// <summary>Downscale-only limits (the picture is never enlarged and keeps its aspect ratio).</summary>
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    /// <summary>Exact frame size (letterbox / crop / stretch). When set, <see cref="MaxWidth"/> and <see cref="MaxHeight"/> are ignored.</summary>
    public CanvasSize? Canvas { get; init; }
    /// <summary>Frame-rate cap: only applied when the source runs faster.</summary>
    public double? MaxFps { get; init; }

    public AudioCodec AudioCodec { get; init; } = AudioCodec.Aac;
    public int AudioBitrateKbps { get; init; } = 128;
    /// <summary>Downmix to this many channels (1 or 2). Null keeps the source layout.</summary>
    public int? AudioChannels { get; init; }
    /// <summary>Drop the audio track.</summary>
    public bool RemoveAudio { get; init; }
    /// <summary>Write only the sound: the container is chosen from <see cref="AudioCodec"/> (m4a, mp3, opus, flac).</summary>
    public bool ExtractAudioOnly { get; init; }

    public TimeSpan? TrimStart { get; init; }
    public TimeSpan? TrimEnd { get; init; }
    public bool Deinterlace { get; init; }

    public bool KeepMetadata { get; init; } = true;
    /// <summary>Move the MP4/MOV index to the front so the file starts playing while it downloads.</summary>
    public bool FastStart { get; init; } = true;

    /// <summary>Extra video filters placed first in the chain (crop, setpts, drawbox ...). Already-escaped filter text.</summary>
    public IReadOnlyList<string> VideoFilters { get; init; } = [];
    /// <summary>Extra audio filters (atempo ...). Already-escaped filter text.</summary>
    public IReadOnlyList<string> AudioFilters { get; init; } = [];
}
