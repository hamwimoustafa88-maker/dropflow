// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Engines.Media.Ffmpeg;

namespace DropFlow.Engines.Media.Preview;

/// <summary>
/// Pure command and filter builders for what the media editors show and play: stills, the waveform, and short listening renders.
/// Nothing here writes next to the user's file; previews go to a scratch folder chosen by the caller.
/// </summary>
internal static class PreviewArgs
{
    /// <summary>Samples per second of the decoded waveform: plenty for drawing bars, small enough for hour-long files.</summary>
    public const int WaveformRate = 4000;

    /// <summary>
    /// One frame at <paramref name="seconds"/> as a PNG on stdout. Input seeking (<c>-ss</c> before <c>-i</c>) is fast on long files and,
    /// because the frame is decoded, still accurate. Pixels are made square first so anamorphic video is not shown squeezed.
    /// </summary>
    public static IReadOnlyList<string> Frame(string input, double seconds, int height) =>
    [
        "-hide_banner", "-nostdin", "-v", "error",
        "-ss", TimeSpec.Format(Math.Max(0, seconds)), "-i", input,
        "-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-dn",
        "-vf", $"scale=trunc(ih*dar/2)*2:ih,setsar=1,scale=-2:{Math.Max(2, ToolFilters.Even(height))}",
        "-f", "image2pipe", "-vcodec", "png", "-",
    ];

    /// <summary>The first audio stream decoded to mono 32-bit float samples at <see cref="WaveformRate"/> on stdout.</summary>
    public static IReadOnlyList<string> Waveform(string input) =>
    [
        "-hide_banner", "-nostdin", "-v", "error", "-i", input,
        "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", WaveformRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "f32le", "-acodec", "pcm_f32le", "-",
    ];

    /// <summary>
    /// A short AAC file for listening: <paramref name="length"/> seconds from <paramref name="start"/> (both input options, so only that
    /// window is read), through <paramref name="filter"/> when given. A null length means to the end.
    /// </summary>
    public static IReadOnlyList<string> AudioSnippet(string input, double start, double? length, string? filter, string output)
    {
        var a = FfmpegCommand.Global();
        if (start > 0) a.AddRange(["-ss", TimeSpec.Format(start)]);
        if (length is > 0) a.AddRange(["-t", TimeSpec.Format(length.Value)]);
        a.AddRange(["-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn"]);
        if (!string.IsNullOrEmpty(filter)) a.AddRange(["-af", filter]);
        a.AddRange(["-c:a", "aac", "-b:a", "160k", output]);
        return a;
    }

    /// <summary>Times in the middle of <paramref name="count"/> equal slots of the file, for the thumbnail strip.</summary>
    public static IReadOnlyList<double> ThumbnailTimes(double duration, int count)
    {
        if (duration <= 0 || count <= 0) return [];
        return [.. Enumerable.Range(0, count).Select(i => Math.Round(duration * (i + 0.5) / count, 3))];
    }

    /// <summary>
    /// The <c>aud.bleep</c> filter for a listening window: the ranges that touch the window are moved to the window's own clock and clipped
    /// to it. Null when no range falls in the window (the preview then plays the original). Surround is brought to stereo like the tool does.
    /// </summary>
    public static string? BleepWindowFilter(IReadOnlyList<(double Start, double End)> ranges, int channels, double frequencyHz, double volume, double windowStart, double windowLength)
    {
        var windowEnd = windowStart + windowLength;
        var inside = ranges
            .Where(r => r.End > windowStart && r.Start < windowEnd)
            .Select(r => new TimeRange(Math.Max(r.Start, windowStart) - windowStart, Math.Min(r.End, windowEnd) - windowStart))
            .Where(r => r.Length > 0.001)
            .ToList();
        if (inside.Count == 0) return null;
        var bleep = ToolFilters.Bleep(TimeSpec.Merge(inside), Math.Min(Math.Max(channels, 1), 2), frequencyHz, volume);
        return channels > 2 ? "aformat=channel_layouts=stereo," + bleep : bleep;
    }

    /// <summary>One side of a stereo file in both ears ("left" or "right"); null for anything else or when there are no sides.</summary>
    public static string? SoloFilter(string listen, int channels)
    {
        if (channels < 2) return null;
        var downmix = channels > 2 ? "aformat=channel_layouts=stereo," : "";
        return listen switch
        {
            "left" => downmix + "pan=stereo|c0=c0|c1=c0",
            "right" => downmix + "pan=stereo|c0=c1|c1=c1",
            _ => null,
        };
    }
}
