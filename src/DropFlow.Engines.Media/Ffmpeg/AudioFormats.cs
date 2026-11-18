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
/// (Vorbis, Opus: experimental) and otherwise yields an error naming the encoder. Conversions can also write m4r (an iPhone
/// ringtone: AAC in the ipod muxer) and ac3 (FFmpeg's native AC-3 encoder), and m4a as lossless ALAC when asked.
/// </summary>
internal static class AudioFormats
{
    /// <summary>The formats the audio tools read and write back (a tool keeps the input's format).</summary>
    public static readonly IReadOnlyList<string> Extensions = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma", "aac"];

    /// <summary>Every format a conversion can write: the tool formats plus the iPhone ringtone and AC-3.</summary>
    public static readonly IReadOnlyList<string> OutputExtensions = [.. Extensions, "m4r", "ac3"];

    /// <summary>
    /// Formats FFmpeg reads with its own decoders (always present, also in the "essentials" build the installer ships) but that the tools
    /// cannot write back: AMR (phone recordings), Monkey's Audio, WavPack, TTA, DSD (dsf/dff), AC-3, DTS, Matroska audio, Core Audio and
    /// iPhone ringtones. They are conversion sources only.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnlyInputs = ["amr", "ape", "wv", "tta", "dsf", "dff", "ac3", "dts", "mka", "caf", "m4r"];

    public static bool IsAudioExtension(string ext) => Extensions.Contains(ext);

    /// <summary>True for every audio file a conversion accepts (the tool formats and the read-only sources).</summary>
    public static bool IsConvertibleAudio(string ext) => Extensions.Contains(ext) || ReadOnlyInputs.Contains(ext);

    public static bool IsOutputExtension(string ext) => OutputExtensions.Contains(ext);

    public static bool IsLossless(string ext) => ext is "wav" or "flac" or "aiff";

    /// <summary>One-bit DSD audio (Super Audio CD rips): decoded at 352.8 kHz or more, which most encoders cannot take.</summary>
    public static bool IsDsd(string ext) => ext is "dsf" or "dff";

    /// <summary>The sample rate a DSD source is resampled to for <paramref name="outputExtension"/>: 88.2 kHz for lossless, else 44.1 (Opus: 48).</summary>
    public static int DsdTargetRate(string outputExtension) => outputExtension switch
    {
        "wav" or "flac" or "aiff" => 88200,
        "opus" => 48000,
        _ => 44100,
    };

    /// <summary>The longest ringtone an iPhone accepts, in seconds.</summary>
    public const int RingtoneMaxSeconds = 40;

    /// <summary>Formats that can carry cover art as an attached picture stream.</summary>
    public static bool CarriesCoverArt(string ext) => ext is "mp3" or "m4a" or "flac";

    /// <summary>The largest channel count the format's encoder accepts (null = no practical limit).</summary>
    public static int? MaxChannels(string ext) => ext switch
    {
        "mp3" or "wma" => 2,
        "ac3" => 6,
        _ => null,
    };

    /// <param name="bitrateKbps">Wanted bitrate; null = the format's default for <paramref name="quality"/>. Ignored by lossless formats.</param>
    /// <param name="alac">m4a only: lossless Apple Lossless instead of AAC.</param>
    public static AudioEncoding Encode(string outputExtension, int? bitrateKbps, AudioQuality quality, MediaInfo? source, FfmpegEncoders encoders, bool alac = false)
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

            case "m4a" when alac:
                // FFmpeg's own encoder (always present): 16-bit sources stay 16-bit, deeper ones are kept at up to 32 bits.
                encoder = "alac";
                args.AddRange(["-c:a", encoder]);
                break;

            case "m4a":
            case "aac":
                encoder = "aac";
                args.AddRange(["-c:a", encoder, "-b:a", $"{kbps ?? (high ? 256 : 192)}k"]);
                break;

            case "m4r":
                // An iPhone ringtone is an AAC .m4a under another name; FFmpeg does not know the extension, so the muxer is named.
                encoder = "aac";
                args.AddRange(["-c:a", encoder, "-b:a", $"{kbps ?? 192}k", "-f", "ipod"]);
                break;

            case "ac3":
                // Native encoder (always present). 448 kb/s is the DVD rate for 5.1; 192 kb/s is plenty for stereo.
                encoder = "ac3";
                args.AddRange(["-c:a", encoder, "-b:a", $"{Math.Min(kbps ?? ((srcChannels ?? 2) > 2 ? 448 : 192), 640)}k"]);
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
