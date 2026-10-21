using DropFlow.Engines.Media.Encoding;

namespace DropFlow.Engines.Media.Ffmpeg;

internal enum AudioQuality
{
    /// <summary>What a plain format conversion uses.</summary>
    Standard,
    /// <summary>For tools that re-encode already-compressed audio (normalise, channels, join): spend a few more bits to avoid generation loss.</summary>
    High,
}

/// <summary>Encoder arguments for one audio output format, or the reason it cannot be produced.</summary>
internal sealed record AudioEncoding(IReadOnlyList<string> Args, string Encoder, IReadOnlyList<string> Warnings, string? Error = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Audio containers DropFlow reads and writes, and how each is encoded:
/// mp3 libmp3lame, m4a/aac AAC, ogg libvorbis, opus libopus, wma wmav2, flac, wav pcm_s16le, aiff pcm_s16be
/// (24-bit PCM when the source has more than 16 bits). A missing encoder degrades to FFmpeg's native one where that exists
/// (Vorbis, Opus: experimental) and otherwise yields an error naming the encoder.
/// </summary>
internal static class AudioFormats
{
    public static readonly IReadOnlyList<string> Extensions = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"];

    public static bool IsAudioExtension(string ext) => Extensions.Contains(ext);

    public static bool IsLossless(string ext) => ext is "wav" or "flac" or "aiff";

    /// <summary>Formats that can carry cover art as an attached picture stream.</summary>
    public static bool CarriesCoverArt(string ext) => ext is "mp3" or "m4a" or "flac";

    /// <summary>The largest channel count the format's encoder accepts (null = no practical limit).</summary>
    public static int? MaxChannels(string ext) => ext is "mp3" or "wma" ? 2 : null;

    /// <param name="bitrateKbps">Wanted bitrate; null = the format's default for <paramref name="quality"/>. Ignored by lossless formats.</param>
    public static AudioEncoding Encode(string outputExtension, int? bitrateKbps, AudioQuality quality, MediaInfo? source, FfmpegEncoders encoders)
    {
        var warnings = new List<string>();
        var args = new List<string>();
        var high = quality == AudioQuality.High;
        var kbps = bitrateKbps is > 0 ? Math.Clamp(bitrateKbps.Value, 16, 512) : (int?)null;
        var srcChannels = source?.Audio?.Channels;
        var wide = (source?.Audio?.BitDepth ?? 16) > 16;
        string encoder;

        switch (outputExtension)
        {
            case "mp3":
                if (!encoders.Has("libmp3lame"))
                    return Fail("This FFmpeg build has no MP3 encoder (libmp3lame). Choose another format or install a full FFmpeg build.");
                encoder = "libmp3lame";
                args.AddRange(["-c:a", encoder]);
                if (kbps is { } mp3) args.AddRange(["-b:a", $"{Math.Min(mp3, 320)}k"]); else args.AddRange(["-q:a", high ? "0" : "2"]);
                args.AddRange(["-id3v2_version", "3", "-write_id3v1", "1"]);
                break;

            case "m4a":
            case "aac":
                encoder = "aac";
                args.AddRange(["-c:a", encoder, "-b:a", $"{kbps ?? (high ? 256 : 192)}k"]);
                break;

            case "ogg":
                if (encoders.Has("libvorbis"))
                {
                    encoder = "libvorbis";
                    args.AddRange(["-c:a", encoder]);
                    if (kbps is { } ogg) args.AddRange(["-b:a", $"{ogg}k"]); else args.AddRange(["-q:a", high ? "7" : "5"]);
                }
                else if (encoders.Has("vorbis"))
                {
                    encoder = "vorbis";
                    warnings.Add("libvorbis is missing; FFmpeg's experimental native Vorbis encoder is used.");
                    args.AddRange(["-c:a", "vorbis", "-strict", "-2", "-b:a", $"{kbps ?? (high ? 256 : 160)}k"]);
                }
                else
                {
                    return Fail("This FFmpeg build has no Vorbis encoder (libvorbis).");
                }
                break;

            case "opus":
                if (encoders.Has("libopus"))
                {
                    encoder = "libopus";
                    args.AddRange(["-c:a", encoder, "-b:a", $"{Math.Min(kbps ?? (high ? 192 : 128), 510)}k", "-vbr", "on"]);
                }
                else if (encoders.Has("opus"))
                {
                    encoder = "opus";
                    warnings.Add("libopus is missing; FFmpeg's experimental native Opus encoder is used.");
                    args.AddRange(["-c:a", "opus", "-strict", "-2", "-b:a", $"{Math.Min(kbps ?? (high ? 192 : 128), 510)}k"]);
                }
                else
                {
                    return Fail("This FFmpeg build has no Opus encoder (libopus).");
                }
                break;

            case "wma":
                encoder = "wmav2";
                args.AddRange(["-c:a", encoder, "-b:a", $"{kbps ?? (high ? 256 : 192)}k"]);
                break;

            case "flac":
                encoder = "flac";
                args.AddRange(["-c:a", encoder, "-compression_level", "5"]);
                break;

            case "wav":
                encoder = wide ? "pcm_s24le" : "pcm_s16le";
                args.AddRange(["-c:a", encoder]);
                break;

            case "aiff":
                encoder = wide ? "pcm_s24be" : "pcm_s16be";
                args.AddRange(["-c:a", encoder]);
                break;

            default:
                return Fail($"'.{outputExtension}' is not a supported audio output.");
        }

        if (MaxChannels(outputExtension) is { } max && srcChannels > max) args.AddRange(["-ac", max.ToString()]);
        return new AudioEncoding(args, encoder, warnings);

        static AudioEncoding Fail(string error) => new([], "", [], error);
    }
}
