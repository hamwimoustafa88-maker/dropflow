using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DropFlow.AI.Speech;

/// <summary>Pure writers: transcript in, file text out. Nothing here touches the disk.</summary>
public static class TranscriptWriters
{
    /// <summary>"01:02:03,456" (SRT). Rounded to the nearest millisecond, so 59.9996 s becomes 00:01:00,000 rather than 00:00:59,1000.</summary>
    public static string SrtTimestamp(TimeSpan time) => Format(time, ',');

    /// <summary>"01:02:03.456" (WebVTT).</summary>
    public static string VttTimestamp(TimeSpan time) => Format(time, '.');

    private static string Format(TimeSpan time, char separator)
    {
        var totalMs = (long)Math.Round(Math.Max(0, time.TotalMilliseconds), MidpointRounding.AwayFromZero);
        var ms = totalMs % 1000;
        var totalSeconds = totalMs / 1000;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}{separator}{ms:000}");
    }

    /// <summary>SubRip. CRLF line endings, 1-based cue numbers, wrapped and split per <paramref name="options"/>.</summary>
    public static string ToSrt(Transcript transcript, SubtitleOptions? options = null)
    {
        var o = options ?? new SubtitleOptions();
        var sb = new StringBuilder();
        var index = 1;
        foreach (var cue in SegmentSplitter.Split(transcript.Segments, o))
        {
            sb.Append(index++.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(SrtTimestamp(cue.Start)).Append(" --> ").Append(SrtTimestamp(cue.End)).Append("\r\n");
            sb.Append(RtlText.MarkLines(cue.Text, o.RtlMarks).Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    /// <summary>WebVTT. LF line endings; cue text is escaped (&amp;, &lt;, &gt;) as the spec requires.</summary>
    public static string ToVtt(Transcript transcript, SubtitleOptions? options = null)
    {
        var o = options ?? new SubtitleOptions();
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in SegmentSplitter.Split(transcript.Segments, o))
        {
            sb.Append(VttTimestamp(cue.Start)).Append(" --> ").Append(VttTimestamp(cue.End)).Append('\n');
            var text = cue.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            sb.Append(RtlText.MarkLines(text, o.RtlMarks)).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Plain text: one segment per line, no timestamps, no direction marks.</summary>
    public static string ToTxt(Transcript transcript)
    {
        var sb = new StringBuilder();
        foreach (var seg in transcript.Segments)
        {
            var text = TextNormalizer.Normalize(seg.Text);
            if (text.Length > 0) sb.Append(text).Append('\n');
        }
        return sb.ToString();
    }

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        // Keep Arabic readable in the file instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// <c>{ "model", "language", "translatedToEnglish", "duration", "text", "segments": [ { "index", "start", "end", "text" } ] }</c>
    /// with times in seconds (millisecond precision). Segments are the recogniser's own, not the wrapped cues.
    /// </summary>
    public static string ToJson(Transcript transcript)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, JsonOptions))
        {
            w.WriteStartObject();
            if (transcript.Model is not null) w.WriteString("model", transcript.Model);
            if (transcript.Language is not null) w.WriteString("language", transcript.Language);
            w.WriteBoolean("translatedToEnglish", transcript.TranslatedToEnglish);
            w.WriteNumber("duration", Seconds(transcript.Duration));
            w.WriteString("text", string.Join(' ', transcript.Segments.Select(s => TextNormalizer.Normalize(s.Text)).Where(t => t.Length > 0)));
            w.WriteStartArray("segments");
            var index = 1;
            foreach (var seg in transcript.Segments)
            {
                w.WriteStartObject();
                w.WriteNumber("index", index++);
                w.WriteNumber("start", Seconds(seg.Start));
                w.WriteNumber("end", Seconds(seg.End));
                w.WriteString("text", TextNormalizer.Normalize(seg.Text));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(stream.ToArray()) + "\n";
    }

    private static double Seconds(TimeSpan t) => Math.Round(t.TotalSeconds, 3, MidpointRounding.AwayFromZero);

    public static string Render(TranscriptFormat format, Transcript transcript, SubtitleOptions? options = null) => format switch
    {
        TranscriptFormat.Srt => ToSrt(transcript, options),
        TranscriptFormat.Vtt => ToVtt(transcript, options),
        TranscriptFormat.Txt => ToTxt(transcript),
        TranscriptFormat.Json => ToJson(transcript),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
