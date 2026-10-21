using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Engines.Media.Tests;

/// <summary>Builders and canned tool output shared by the tests. The text samples follow the real ffprobe / ffmpeg formats.</summary>
internal static class TestMedia
{
    public static MediaInfo Video(
        int width = 1920, int height = 1080, double fps = 30, double seconds = 60,
        string videoCodec = "h264", string? audioCodec = "aac", int channels = 2, int sampleRate = 48000,
        int rotation = 0, string path = @"C:\Videos\in.mp4", bool hdr = false, long? audioBitRate = 128_000) =>
        new()
        {
            Path = path,
            FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
            DurationSeconds = seconds,
            Streams = new[]
            {
                new StreamInfo
                {
                    Index = 0, Kind = StreamKind.Video, CodecName = videoCodec, Width = width, Height = height, FrameRate = fps,
                    PixelFormat = "yuv420p", Rotation = rotation, DurationSeconds = seconds,
                    ColorTransfer = hdr ? "smpte2084" : "bt709",
                },
            }.Concat(audioCodec is null ? [] : new[]
            {
                new StreamInfo
                {
                    Index = 1, Kind = StreamKind.Audio, CodecName = audioCodec, Channels = channels, SampleRate = sampleRate,
                    BitRate = audioBitRate, DurationSeconds = seconds,
                },
            }).ToList(),
        };

    public static MediaInfo Audio(string codec = "mp3", int channels = 2, int sampleRate = 44100, double seconds = 30, string path = @"C:\Music\in.mp3", bool cover = false) =>
        new()
        {
            Path = path,
            DurationSeconds = seconds,
            Streams = new[]
            {
                new StreamInfo { Index = 0, Kind = StreamKind.Audio, CodecName = codec, Channels = channels, SampleRate = sampleRate, DurationSeconds = seconds },
            }.Concat(cover ? new[] { new StreamInfo { Index = 1, Kind = StreamKind.Video, CodecName = "mjpeg", Width = 500, Height = 500, IsAttachedPicture = true } } : []).ToList(),
        };

    /// <summary>ffprobe -print_format json -show_format -show_streams of an iPhone-style 1080p H.264 + AAC MP4 with Arabic tags.</summary>
    public const string FfprobeMp4 = """
        {
            "streams": [
                {
                    "index": 0,
                    "codec_name": "h264",
                    "codec_long_name": "H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10",
                    "profile": "High",
                    "codec_type": "video",
                    "codec_tag_string": "avc1",
                    "codec_tag": "0x31637661",
                    "width": 1920,
                    "height": 1080,
                    "coded_width": 1920,
                    "coded_height": 1088,
                    "has_b_frames": 2,
                    "sample_aspect_ratio": "1:1",
                    "display_aspect_ratio": "16:9",
                    "pix_fmt": "yuv420p",
                    "level": 40,
                    "color_range": "tv",
                    "color_space": "bt709",
                    "color_transfer": "bt709",
                    "color_primaries": "bt709",
                    "chroma_location": "left",
                    "field_order": "progressive",
                    "refs": 1,
                    "is_avc": "true",
                    "nal_length_size": "4",
                    "id": "0x1",
                    "r_frame_rate": "30000/1001",
                    "avg_frame_rate": "30000/1001",
                    "time_base": "1/30000",
                    "start_pts": 0,
                    "start_time": "0.000000",
                    "duration_ts": 1801800,
                    "duration": "60.060000",
                    "bit_rate": "4800000",
                    "bits_per_raw_sample": "8",
                    "nb_frames": "1800",
                    "extradata_size": 45,
                    "disposition": {
                        "default": 1, "dub": 0, "original": 0, "comment": 0, "lyrics": 0, "karaoke": 0, "forced": 0,
                        "hearing_impaired": 0, "visual_impaired": 0, "clean_effects": 0, "attached_pic": 0,
                        "timed_thumbnails": 0, "non_diegetic": 0, "captions": 0, "descriptions": 0, "metadata": 0,
                        "dependent": 0, "still_image": 0
                    },
                    "tags": { "language": "und", "handler_name": "VideoHandler", "vendor_id": "[0][0][0][0]" },
                    "side_data_list": [
                        { "side_data_type": "Display Matrix", "displaymatrix": "\n00000000:            0       65536           0\n", "rotation": -90 }
                    ]
                },
                {
                    "index": 1,
                    "codec_name": "aac",
                    "profile": "LC",
                    "codec_type": "audio",
                    "sample_fmt": "fltp",
                    "sample_rate": "48000",
                    "channels": 2,
                    "channel_layout": "stereo",
                    "bits_per_sample": 0,
                    "r_frame_rate": "0/0",
                    "avg_frame_rate": "0/0",
                    "duration": "60.032000",
                    "bit_rate": "128000",
                    "disposition": { "default": 1, "attached_pic": 0 },
                    "tags": { "language": "ara", "handler_name": "SoundHandler" }
                },
                {
                    "index": 2,
                    "codec_name": "mov_text",
                    "codec_type": "subtitle",
                    "r_frame_rate": "0/0",
                    "avg_frame_rate": "0/0",
                    "disposition": { "default": 0, "attached_pic": 0 },
                    "tags": { "language": "ara" }
                }
            ],
            "format": {
                "filename": "C:\\Users\\Moustafa\\Videos\\فيديو عائلي.mp4",
                "nb_streams": 3,
                "nb_programs": 0,
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                "format_long_name": "QuickTime / MOV",
                "start_time": "0.000000",
                "duration": "60.060000",
                "size": "37700000",
                "bit_rate": "5021000",
                "probe_score": 100,
                "tags": { "major_brand": "isom", "minor_version": "512", "compatible_brands": "isomiso2avc1mp41", "title": "عطلة الصيف", "ARTIST": "موسى", "encoder": "Lavf60.16.100" }
            }
        }
        """;

    /// <summary>A Matroska file: no container duration, stream durations only as DURATION tags, VFR video.</summary>
    public const string FfprobeMkv = """
        {
            "streams": [
                {
                    "index": 0, "codec_name": "hevc", "codec_type": "video", "width": 3840, "height": 2160,
                    "pix_fmt": "yuv420p10le", "r_frame_rate": "60/1", "avg_frame_rate": "30000/1001",
                    "color_transfer": "smpte2084", "field_order": "tt",
                    "disposition": { "default": 1, "attached_pic": 0 },
                    "tags": { "DURATION": "00:02:30.500000000", "BPS": "25000000" }
                },
                {
                    "index": 1, "codec_name": "opus", "codec_type": "audio", "sample_rate": "48000", "channels": 6,
                    "channel_layout": "5.1(side)",
                    "disposition": { "default": 1, "attached_pic": 0 },
                    "tags": { "language": "eng", "DURATION": "00:02:30.501000000" }
                }
            ],
            "format": { "filename": "movie.mkv", "format_name": "matroska,webm", "size": "500000000", "bit_rate": "26600000" }
        }
        """;

    /// <summary>An MP3 with cover art stored as an attached picture.</summary>
    public const string FfprobeMp3WithCover = """
        {
            "streams": [
                { "index": 0, "codec_name": "mp3", "codec_type": "audio", "sample_rate": "44100", "channels": 2, "channel_layout": "stereo",
                  "duration": "215.248980", "bit_rate": "320000", "disposition": { "default": 0, "attached_pic": 0 } },
                { "index": 1, "codec_name": "mjpeg", "codec_type": "video", "width": 600, "height": 600, "pix_fmt": "yuvj444p",
                  "r_frame_rate": "90000/1", "avg_frame_rate": "0/0", "disposition": { "default": 0, "attached_pic": 1 } }
            ],
            "format": { "filename": "song.mp3", "format_name": "mp3", "duration": "215.248980", "size": "8610000", "bit_rate": "320000",
                        "tags": { "title": "Song", "artist": "Artist", "album": "Album" } }
        }
        """;

    /// <summary>ffmpeg -hide_banner -encoders (shortened to the lines that matter, with the real header).</summary>
    public const string EncodersGpl = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 1
         ------
         V....D a64multi             Multicolor charset for Commodore 64 (codec a64_multi)
         V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V..... libx265              libx265 H.265 / HEVC (codec hevc)
         V....D libvpx-vp9           libvpx VP9 (codec vp9)
         V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
         V....D av1_nvenc            NVIDIA NVENC av1 encoder (codec av1)
         V..... h264_qsv             H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (Intel Quick Sync Video acceleration) (codec h264)
         V..... hevc_qsv             HEVC (Intel Quick Sync Video acceleration) (codec hevc)
         V....D h264_amf             AMD AMF H.264 Encoder (codec h264)
         V....D hevc_amf             AMD AMF HEVC encoder (codec hevc)
         V....D mpeg4                MPEG-4 part 2
         V....D wmv2                 Windows Media Video 8
         A....D aac                  AAC (Advanced Audio Coding)
         A....D flac                 FLAC (Free Lossless Audio Codec)
         A..... libmp3lame           libmp3lame MP3 (MPEG audio layer 3) (codec mp3)
         A....D libopus              libopus Opus (codec opus)
         A..... libvorbis            libvorbis (codec vorbis)
         A....D wmav2                Windows Media Audio 2
         A....D pcm_s16le            PCM signed 16-bit little-endian
         A....D pcm_s16be            PCM signed 16-bit big-endian
         S..... ass                  ASS (Advanced SubStation Alpha) subtitle (codec ass)
        """;

    /// <summary>A build with no x264/x265/VP9/AV1/lame/opus: only FFmpeg's own encoders.</summary>
    public const string EncodersMinimal = """
        Encoders:
         V..... = Video
         ------
         V....D mpeg4                MPEG-4 part 2
         A....D aac                  AAC (Advanced Audio Coding)
         A....D flac                 FLAC (Free Lossless Audio Codec)
         A....D pcm_s16le            PCM signed 16-bit little-endian
        """;

    public static EncoderSettings Defaults => new() { Hardware = HardwarePreference.None };

    public static PlanContext Ctx(string? output = @"C:\Out\out.mp4", bool allHardware = false) =>
        new()
        {
            OutputPath = output,
            Encoders = FfmpegEncoders.Parse(EncodersGpl),
            Hardware = allHardware ? HardwareEncoders.Detect(EncodersGpl) : null,
        };
}
