using System.Text;
using System.Text.RegularExpressions;

namespace DropFlow.AI.Speech;

/// <summary>Cleans raw recogniser text so it reads well in subtitles and plain text.</summary>
public static class TextNormalizer
{
    // Whisper emits these for silence and they are never useful in a transcript.
    private static readonly Regex NonSpeechTag =
        new(@"[\[\(]\s*(BLANK_AUDIO|silence|silent)\s*[\]\)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Space before closing punctuation, Latin and Arabic: "hello ," -> "hello,"  and before the Arabic comma, semicolon,
    // question mark and the ellipsis character (code points 060C, 061B, 061F, 2026).
    private static readonly Regex SpaceBeforePunctuation =
        new(@"\s+([,.;:!?" + (char)0x060C + (char)0x061B + (char)0x061F + (char)0x2026 + "])", RegexOptions.Compiled);

    /// <summary>
    /// Trims, collapses all whitespace (including line breaks) to single spaces, removes invisible junk (zero-width spaces, BOMs,
    /// replacement characters and any bidi control characters; the writers add their own marks), removes silence tags and
    /// closes up space before punctuation. Joiners (U+200C/U+200D) are kept because Persian and Arabic need them.
    /// </summary>
    public static string Normalize(string? text, bool stripNonSpeechTags = true)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (IsInvisibleJunk(ch)) continue;
            if (char.IsWhiteSpace(ch)) sb.Append(' ');
            else if (!char.IsControl(ch)) sb.Append(ch);
        }

        var s = sb.ToString();
        if (stripNonSpeechTags) s = NonSpeechTag.Replace(s, " ");
        s = Whitespace.Replace(s, " ").Trim();
        s = SpaceBeforePunctuation.Replace(s, "$1");
        return s;
    }

    private static bool IsInvisibleJunk(char c) =>
        c is (char)0x200B       // zero-width space
        or (char)0xFEFF         // byte order mark / zero-width no-break space
        or (char)0xFFFD         // replacement character
        or (char)0x200E or (char)0x200F             // LRM, RLM
        || (c >= (char)0x202A && c <= (char)0x202E) // LRE RLE PDF LRO RLO
        || (c >= (char)0x2066 && c <= (char)0x2069); // LRI RLI FSI PDI

    /// <summary>
    /// Normalises every segment, drops the ones left empty, orders by start time and repairs timing
    /// (end never before start; zero-length segments get <paramref name="minDuration"/>).
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Clean(IEnumerable<TranscriptSegment> segments, TimeSpan? minDuration = null, bool stripNonSpeechTags = true)
    {
        var min = minDuration ?? TimeSpan.FromMilliseconds(500);
        var result = new List<TranscriptSegment>();
        foreach (var seg in segments)
        {
            var text = Normalize(seg.Text, stripNonSpeechTags);
            if (text.Length == 0) continue;
            var start = seg.Start < TimeSpan.Zero ? TimeSpan.Zero : seg.Start;
            var end = seg.End > start ? seg.End : start + min;
            result.Add(new TranscriptSegment(start, end, text));
        }
        return result.OrderBy(s => s.Start).ToList();
    }
}
