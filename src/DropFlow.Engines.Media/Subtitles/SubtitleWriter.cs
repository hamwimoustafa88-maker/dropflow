using System.Globalization;
using System.Text;

namespace DropFlow.Engines.Media.Subtitles;

/// <summary>Writes a <see cref="SubtitleDocument"/> as SRT, WebVTT, ASS, plain text, LRC lyrics or YouTube SBV.</summary>
public static class SubtitleWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>The formats this writer produces (SAMI, TTML and MicroDVD are read only).</summary>
    public static IReadOnlyList<SubtitleFormat> Writable { get; } =
        [SubtitleFormat.Srt, SubtitleFormat.Vtt, SubtitleFormat.Ass, SubtitleFormat.Txt, SubtitleFormat.Lrc, SubtitleFormat.Sbv];

    public static string Write(SubtitleDocument doc, SubtitleFormat target, SubtitleWriteOptions? options = null)
    {
        options ??= new SubtitleWriteOptions();
        var nl = options.NewLine ?? doc.NewLine;
        return target switch
        {
            SubtitleFormat.Srt => WriteSrt(doc, nl),
            SubtitleFormat.Vtt => WriteVtt(doc, nl),
            SubtitleFormat.Ass => WriteAss(doc, nl),
            SubtitleFormat.Txt => WriteTxt(doc, nl, options.CollapseRepeats),
            SubtitleFormat.Lrc => WriteLrc(doc, nl),
            SubtitleFormat.Sbv => WriteSbv(doc, nl),
            _ => throw new ArgumentException($"{target} is a format DropFlow reads but does not write.", nameof(target)),
        };
    }

    /// <summary>Text to bytes (UTF-8), with a BOM according to <see cref="SubtitleWriteOptions.Bom"/> and the format's default.</summary>
    public static byte[] ToBytes(string text, SubtitleFormat target, SubtitleWriteOptions? options = null)
    {
        var bom = options?.Bom ?? target is not (SubtitleFormat.Vtt or SubtitleFormat.Sbv);
        var body = Utf8NoBom.GetBytes(text);
        if (!bom) return body;
        return [0xEF, 0xBB, 0xBF, .. body];
    }

    /// <summary>Convenience: bytes of one format to bytes of another (parse, write, encode).</summary>
    public static byte[] Convert(byte[] input, SubtitleFormat from, SubtitleFormat to, SubtitleWriteOptions? options = null)
    {
        var doc = SubtitleParser.ParseBytes(input, from);
        return ToBytes(Write(doc, to, options), to, options);
    }

    // ------------------------------------------------------------------ SRT

    private static string WriteSrt(SubtitleDocument doc, string nl)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (var cue in doc.Cues)
        {
            sb.Append(n++.ToString(CultureInfo.InvariantCulture)).Append(nl);
            sb.Append(Clock(cue.Start, ',')).Append(" --> ").Append(Clock(cue.End, ',')).Append(nl);
            sb.Append(SubtitleText.TidyLines(cue.Text).Replace("\n", nl, StringComparison.Ordinal)).Append(nl).Append(nl);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ WebVTT

    private static string WriteVtt(SubtitleDocument doc, string nl)
    {
        var sb = new StringBuilder("WEBVTT").Append(nl).Append(nl);
        foreach (var cue in doc.Cues)
        {
            sb.Append(Clock(cue.Start, '.')).Append(" --> ").Append(Clock(cue.End, '.')).Append(nl);
            sb.Append(SubtitleText.ToVtt(SubtitleText.TidyLines(cue.Text)).Replace("\n", nl, StringComparison.Ordinal)).Append(nl).Append(nl);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ ASS

    private static string WriteAss(SubtitleDocument doc, string nl)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append(nl);

        Line("[Script Info]");
        Line("; Converted by DropFlow");
        Line("Title: " + SingleLine(doc.Title ?? "DropFlow subtitles"));
        Line("ScriptType: v4.00+");
        Line("WrapStyle: 0");
        Line("ScaledBorderAndShadow: yes");
        Line("PlayResX: 1920");
        Line("PlayResY: 1080");
        Line("");
        Line("[V4+ Styles]");
        Line("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        Line("Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,3,1,2,40,40,50,1");
        Line("");
        Line("[Events]");
        Line("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        foreach (var cue in doc.Cues)
        {
            // Source style names are not carried over: only "Default" is defined in the output, and an undefined name would fall back to it anyway.
            const string style = "Default";
            var name = SingleLine(cue.Actor ?? "").Replace(",", "", StringComparison.Ordinal);
            Line($"Dialogue: 0,{AssClock(cue.Start)},{AssClock(cue.End)},{style},{name},0,0,0,,{SubtitleText.ToAss(SubtitleText.TidyLines(cue.Text))}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ TXT

    private static string WriteTxt(SubtitleDocument doc, string nl, bool collapseRepeats)
    {
        var sb = new StringBuilder();
        string? previous = null;
        foreach (var cue in doc.Cues)
        {
            var line = SubtitleText.ToPlain(SubtitleText.TidyLines(cue.Text)).Replace("\n", " ", StringComparison.Ordinal).Trim();
            if (line.Length == 0) continue;
            if (collapseRepeats && line == previous) continue;
            previous = line;
            sb.Append(line).Append(nl);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ LRC

    /// <summary>
    /// <c>[mm:ss.xx]text</c>, one line per cue (LRC has no line breaks or styling: lines are joined with a space, tags dropped).
    /// LRC has no end times, so an empty <c>[mm:ss.xx]</c> line clears the screen where a gap follows a cue and after the last one.
    /// </summary>
    private static string WriteLrc(SubtitleDocument doc, string nl)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(doc.Title)) sb.Append("[ti:").Append(SingleLine(doc.Title).Replace("]", ")", StringComparison.Ordinal)).Append(']').Append(nl);
        var cues = doc.Cues.Where(c => SubtitleText.ToPlain(c.Text).Trim().Length > 0).ToList();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var line = SingleLine(SubtitleText.ToPlain(SubtitleText.TidyLines(cue.Text)));
            sb.Append('[').Append(LrcClock(cue.Start)).Append(']').Append(line).Append(nl);
            var next = i + 1 < cues.Count ? cues[i + 1].Start : (TimeSpan?)null;
            if (next is null || LrcClock(next.Value) != LrcClock(cue.End) && next.Value > cue.End)
                sb.Append('[').Append(LrcClock(cue.End)).Append(']').Append(nl);
        }
        return sb.ToString();
    }

    /// <summary>LRC time <c>mm:ss.xx</c> (hundredths, rounded); minutes grow past 99 when needed.</summary>
    public static string LrcClock(TimeSpan t)
    {
        var cs = (long)Math.Round(t.TotalMilliseconds / 10.0, MidpointRounding.AwayFromZero);
        if (cs < 0) cs = 0;
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 6000:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    // ------------------------------------------------------------------ SBV

    /// <summary>YouTube SBV: <c>H:MM:SS.mmm,H:MM:SS.mmm</c>, the text lines (no markup), a blank line.</summary>
    private static string WriteSbv(SubtitleDocument doc, string nl)
    {
        var sb = new StringBuilder();
        foreach (var cue in doc.Cues)
        {
            var text = SubtitleText.TidyLines(SubtitleText.ToPlain(cue.Text));
            if (text.Length == 0) continue;
            sb.Append(SbvClock(cue.Start)).Append(',').Append(SbvClock(cue.End)).Append(nl);
            sb.Append(text.Replace("\n", nl, StringComparison.Ordinal)).Append(nl).Append(nl);
        }
        return sb.ToString();
    }

    /// <summary>SBV time <c>H:MM:SS.mmm</c>.</summary>
    public static string SbvClock(TimeSpan t)
    {
        var ms = (long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);
        if (ms < 0) ms = 0;
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }

    // ------------------------------------------------------------------ time

    /// <summary>"HH:MM:SS,mmm" (or '.' for WebVTT). Hours grow past two digits when needed; milliseconds are exact.</summary>
    public static string Clock(TimeSpan t, char decimalSeparator)
    {
        var ms = (long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);
        if (ms < 0) ms = 0;
        var h = ms / 3_600_000;
        var m = ms / 60_000 % 60;
        var s = ms / 1000 % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{decimalSeparator}{ms % 1000:000}");
    }

    /// <summary>ASS time <c>H:MM:SS.cc</c>: centiseconds, rounded to the nearest (ASS cannot store milliseconds).</summary>
    public static string AssClock(TimeSpan t)
    {
        var cs = (long)Math.Round(t.TotalMilliseconds / 10.0, MidpointRounding.AwayFromZero);
        if (cs < 0) cs = 0;
        var h = cs / 360_000;
        var m = cs / 6000 % 60;
        var s = cs / 100 % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{cs % 100:00}");
    }

    private static string SingleLine(string s) => s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
