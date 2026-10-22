using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Media.Ffmpeg;

/// <summary>Parses <c>silencedetect</c> log lines and computes leading/trailing silence for auto-trim.</summary>
internal static partial class SilenceDetect
{
    /// <summary>Builds the analysis filter, e.g. <c>silencedetect=noise=-50dB:d=0.5</c>.</summary>
    public static string Filter(double noiseDb, double minSeconds) =>
        $"silencedetect=noise={TimeSpec.Num(noiseDb, 2)}dB:d={TimeSpec.Num(minSeconds, 3)}";

    /// <summary>
    /// Parses stderr of a <c>silencedetect</c> run. A <c>silence_start</c> without a later <c>silence_end</c> (older FFmpeg
    /// versions do not print the end of trailing silence) is returned with <c>End = null</c>, meaning "until the end of the file".
    /// </summary>
    public static IReadOnlyList<SilenceInterval> Parse(string stderr)
    {
        var result = new List<SilenceInterval>();
        double? openStart = null;
        foreach (var line in stderr.Split('\n'))
        {
            var start = StartRegex().Match(line);
            if (start.Success)
            {
                if (openStart is not null) result.Add(new SilenceInterval(openStart.Value, null));
                openStart = Math.Max(0, ParseNumber(start.Groups[1].Value));
                continue;
            }
            var end = EndRegex().Match(line);
            if (end.Success)
            {
                var e = ParseNumber(end.Groups[1].Value);
                result.Add(new SilenceInterval(openStart ?? 0, e));
                openStart = null;
            }
        }
        if (openStart is not null) result.Add(new SilenceInterval(openStart.Value, null));
        return result;
    }

    /// <summary>
    /// Works out how much silence to cut at the two ends only. Silence in the middle is never touched. Returns null when the
    /// whole file is silent. <paramref name="padding"/> seconds of silence are kept next to the sound so word onsets are not clipped.
    /// </summary>
    public static (double Start, double End)? EdgeTrim(IReadOnlyList<SilenceInterval> silences, double duration, double padding = 0.1, double tolerance = 0.05)
    {
        var start = 0.0;
        var end = duration;
        foreach (var s in silences)
        {
            var sEnd = s.End ?? duration;
            if (s.Start <= tolerance && sEnd >= duration - tolerance) return null; // all silence
            if (s.Start <= tolerance) start = Math.Min(Math.Max(start, sEnd - padding), duration);
            if (sEnd >= duration - tolerance) end = Math.Max(Math.Min(end, s.Start + padding), 0);
        }
        start = Math.Max(0, start);
        return end > start ? (start, end) : null;
    }

    private static double ParseNumber(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"silence_start:\s*(-?[0-9]*\.?[0-9]+(?:[eE][+-]?[0-9]+)?)")]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"silence_end:\s*(-?[0-9]*\.?[0-9]+(?:[eE][+-]?[0-9]+)?)")]
    private static partial Regex EndRegex();
}

/// <summary>Which ends of a recording "trim silence" works on.</summary>
internal enum SilenceEnds { Both, Start, End }

/// <summary>
/// Filters for <c>aud.trim-silence</c>. The start is trimmed with <c>silenceremove</c>; the end is trimmed by reversing the sound, trimming its
/// (new) start and reversing back, because <c>silenceremove</c>'s own "stop" mode also cuts pauses in the middle. 0.1 s of silence is kept next
/// to the sound so the first and last syllables are not clipped. <c>areverse</c> holds the whole decoded track in memory, so recordings longer
/// than <see cref="MaxReverseSeconds"/> are measured with <c>silencedetect</c> first and cut with <c>atrim</c> instead (same result, constant memory).
/// </summary>
internal static class SilenceTrim
{
    /// <summary>10 minutes of 48 kHz stereo float is about 230 MB in memory; longer files take the measure-then-cut path.</summary>
    public const double MaxReverseSeconds = 600;
    public const double KeepSeconds = 0.1;

    public static bool UsesReversal(double? durationSeconds) => durationSeconds is not > MaxReverseSeconds;

    public static string RemoveFilter(double thresholdDb, SilenceEnds ends)
    {
        var remove = $"silenceremove=start_periods=1:start_threshold={TimeSpec.Num(thresholdDb, 2)}dB:start_silence={TimeSpec.Num(KeepSeconds, 3)}";
        return ends switch
        {
            SilenceEnds.Start => remove,
            SilenceEnds.End => $"areverse,{remove},areverse",
            _ => $"{remove},areverse,{remove},areverse",
        };
    }

    /// <summary>The range to keep after applying <paramref name="ends"/> to the measured sound range, or null when nothing would be removed.</summary>
    public static (double Start, double? End)? KeepRange((double Start, double End) sound, double duration, SilenceEnds ends)
    {
        var start = ends == SilenceEnds.End ? 0 : sound.Start;
        double? end = ends == SilenceEnds.Start || sound.End >= duration - 0.01 ? null : sound.End;
        if (start <= 0.01 && end is null) return null;
        return (start <= 0.01 ? 0 : start, end);
    }

    public static string AtrimFilter(double start, double? end)
    {
        var parts = new List<string>();
        if (start > 0) parts.Add($"start={TimeSpec.Num(start, 3)}");
        if (end is { } e) parts.Add($"end={TimeSpec.Num(e, 3)}");
        return $"atrim={string.Join(":", parts)},asetpts=PTS-STARTPTS";
    }
}

/// <summary>A silent stretch. <see cref="End"/> is null when the silence runs to the end of the file.</summary>
internal readonly record struct SilenceInterval(double Start, double? End);

/// <summary>Measured loudness from the first <c>loudnorm</c> pass. Values are kept as FFmpeg printed them.</summary>
internal sealed record LoudnormStats(
    string InputI, string InputTp, string InputLra, string InputThresh, string TargetOffset,
    string? OutputI = null, string? OutputTp = null, string? NormalizationType = null)
{
    /// <summary>True when the measurement is usable. Silent input measures as -inf, which loudnorm cannot normalise.</summary>
    public bool IsUsable =>
        new[] { InputI, InputTp, InputLra, InputThresh, TargetOffset }.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d));
}

/// <summary>EBU R128 two-pass loudness normalisation helpers.</summary>
internal static partial class Loudnorm
{
    public static string Pass1Filter(double target, double truePeak, double lra) =>
        $"loudnorm=I={TimeSpec.Num(target, 2)}:TP={TimeSpec.Num(truePeak, 2)}:LRA={TimeSpec.Num(lra, 2)}:print_format=json";

    /// <summary>
    /// The second pass with the measured values from pass 1, in linear mode (a straight gain, no dynamics change) when possible.
    /// loudnorm silently upsamples to 192 kHz, so the caller must write the output with <c>-ar</c> set to the source rate.
    /// </summary>
    public static string Pass2Filter(LoudnormStats m, double target, double truePeak, double lra) =>
        $"loudnorm=I={TimeSpec.Num(target, 2)}:TP={TimeSpec.Num(truePeak, 2)}:LRA={TimeSpec.Num(lra, 2)}" +
        $":measured_I={m.InputI}:measured_TP={m.InputTp}:measured_LRA={m.InputLra}:measured_thresh={m.InputThresh}" +
        $":offset={m.TargetOffset}:linear=true:print_format=summary";

    /// <summary>Extracts the JSON block that <c>print_format=json</c> prints to stderr. Returns null when it is not there.</summary>
    public static LoudnormStats? ParseStats(string stderr)
    {
        // The block is the last {...} that contains "input_i"; log lines such as "[Parsed_loudnorm_0 @ 0x...]" precede it.
        var from = stderr.LastIndexOf("\"input_i\"", StringComparison.Ordinal);
        if (from < 0) return null;
        var open = stderr.LastIndexOf('{', from);
        var close = stderr.IndexOf('}', from);
        if (open < 0 || close < 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(stderr[open..(close + 1)]);
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var p)
                ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText())
                : null;

            var i = Get("input_i");
            var tp = Get("input_tp");
            var lra = Get("input_lra");
            var thresh = Get("input_thresh");
            var offset = Get("target_offset");
            if (i is null || tp is null || lra is null || thresh is null || offset is null) return null;
            return new LoudnormStats(i, tp, lra, thresh, offset, Get("output_i"), Get("output_tp"), Get("normalization_type"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
