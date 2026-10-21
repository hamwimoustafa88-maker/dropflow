namespace DropFlow.AI.Speech;

/// <summary>One recognised phrase with its position in the audio. Text may contain '\n' once it has been wrapped for display.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>Everything the writers need to know about one transcription run.</summary>
public sealed record Transcript(
    IReadOnlyList<TranscriptSegment> Segments,
    string? Language = null,
    string? Model = null,
    bool TranslatedToEnglish = false)
{
    /// <summary>End of the last segment.</summary>
    public TimeSpan Duration => Segments.Count == 0 ? TimeSpan.Zero : Segments.Max(s => s.End);
}

/// <summary>Output formats the transcribe operations can write.</summary>
public enum TranscriptFormat { Srt, Vtt, Txt, Json }

public static class TranscriptFormats
{
    public static string Extension(TranscriptFormat format) => format switch
    {
        TranscriptFormat.Srt => "srt",
        TranscriptFormat.Vtt => "vtt",
        TranscriptFormat.Txt => "txt",
        TranscriptFormat.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Parses "srt", "srt,txt", "srt+vtt", "all" (case-insensitive). Unknown names are ignored; the result is never empty (falls back to SRT).</summary>
    public static IReadOnlyList<TranscriptFormat> Parse(string? value)
    {
        var result = new List<TranscriptFormat>();
        foreach (var part in (value ?? "").Split([',', '+', ';', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "all":
                    return [TranscriptFormat.Srt, TranscriptFormat.Vtt, TranscriptFormat.Txt, TranscriptFormat.Json];
                case "srt": Add(TranscriptFormat.Srt); break;
                case "vtt": Add(TranscriptFormat.Vtt); break;
                case "txt": Add(TranscriptFormat.Txt); break;
                case "json": Add(TranscriptFormat.Json); break;
            }
        }
        return result.Count == 0 ? [TranscriptFormat.Srt] : result;

        void Add(TranscriptFormat f)
        {
            if (!result.Contains(f)) result.Add(f);
        }
    }
}
