namespace DropFlow.Engines.Media.Subtitles;

public enum SubtitleFormat { Srt, Vtt, Ass, Txt }

/// <summary>
/// One caption. <see cref="Text"/> is canonical: plain text with <c>\n</c> line breaks and only the inline tags
/// <c>&lt;i&gt; &lt;b&gt; &lt;u&gt; &lt;s&gt;</c>. Every source format is reduced to this on reading and expanded on writing, so
/// timing and wording are preserved while format-specific styling (ASS overrides, VTT classes, voices) is dropped.
/// </summary>
public sealed record SubtitleCue(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? Identifier = null,
    string? Settings = null,
    string? Style = null,
    string? Actor = null);

public sealed record SubtitleDocument(
    IReadOnlyList<SubtitleCue> Cues,
    SubtitleFormat SourceFormat,
    string NewLine,
    IReadOnlyList<string> Warnings,
    string? Title = null);

/// <summary>Raised for input that is not a subtitle file at all (binary data, no cues). The message is safe to show in a log.</summary>
public sealed class SubtitleFormatException(string message) : Exception(message);

public sealed record SubtitleWriteOptions
{
    /// <summary>Line ending for the output. Null keeps the source file's style.</summary>
    public string? NewLine { get; init; }

    /// <summary>
    /// Write a UTF-8 byte-order mark. Null picks the best default per format: yes for SRT, ASS and TXT (older Windows players and
    /// Notepad need it to show Arabic correctly), no for WebVTT (browsers and the spec prefer none).
    /// </summary>
    public bool? Bom { get; init; }

    /// <summary>TXT only: drop a cue whose text equals the previous cue's (rolling auto-captions repeat lines).</summary>
    public bool CollapseRepeats { get; init; }
}
