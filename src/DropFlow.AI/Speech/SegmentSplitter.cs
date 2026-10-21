using System.Globalization;
using System.Text;

namespace DropFlow.AI.Speech;

/// <param name="MaxCharsPerLine">Longest allowed subtitle line, in user-perceived characters. Typical broadcast value is 42.</param>
/// <param name="MaxLines">Lines per cue; longer segments are split into several cues.</param>
/// <param name="RtlMarks">How right-to-left lines are marked in SRT/VTT.</param>
public sealed record SubtitleOptions(int MaxCharsPerLine = 42, int MaxLines = 2, RtlMarkStyle RtlMarks = RtlMarkStyle.Rlm)
{
    public SubtitleOptions Validated() => this with
    {
        MaxCharsPerLine = Math.Clamp(MaxCharsPerLine, 10, 200),
        MaxLines = Math.Clamp(MaxLines, 1, 6),
    };
}

/// <summary>Greedy word wrapping that understands scripts written without spaces (Chinese, Japanese, Thai...).</summary>
public static class LineWrapper
{
    private readonly record struct Token(string Text, bool SpaceBefore);

    /// <summary>Breaks text into lines of at most <paramref name="maxChars"/> text elements. A single over-long word stays whole.</summary>
    public static IReadOnlyList<string> Wrap(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        maxChars = Math.Max(1, maxChars);

        var lines = new List<string>();
        var line = new StringBuilder();
        var lineLen = 0;
        foreach (var token in Tokenize(text))
        {
            var len = Length(token.Text);
            var add = (lineLen > 0 && token.SpaceBefore ? 1 : 0) + len;
            if (lineLen > 0 && lineLen + add > maxChars)
            {
                lines.Add(line.ToString());
                line.Clear();
                lineLen = 0;
                add = len;
            }
            if (lineLen > 0 && token.SpaceBefore) line.Append(' ');
            line.Append(token.Text);
            lineLen += add;
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    public static int Length(string s) => new StringInfo(s).LengthInTextElements;

    private static IEnumerable<Token> Tokenize(string text)
    {
        var word = new StringBuilder();
        var spaceBefore = false;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (element.Length == 1 && char.IsWhiteSpace(element[0]))
            {
                if (word.Length > 0) { yield return new Token(word.ToString(), spaceBefore); word.Clear(); }
                spaceBefore = true;
                continue;
            }

            // Every character of an unspaced script is a legal break point.
            if (BreaksAnywhere(element))
            {
                if (word.Length > 0) { yield return new Token(word.ToString(), spaceBefore); word.Clear(); spaceBefore = false; }
                yield return new Token(element, spaceBefore);
                spaceBefore = false;
                continue;
            }
            word.Append(element);
        }
        if (word.Length > 0) yield return new Token(word.ToString(), spaceBefore);
    }

    private static bool BreaksAnywhere(string element)
    {
        if (!Rune.TryGetRuneAt(element, 0, out var rune)) return false; // lone surrogate: treat as an ordinary character
        var cp = rune.Value;
        return cp is >= 0x3040 and <= 0x30FF       // Hiragana, Katakana
            or >= 0x3400 and <= 0x4DBF             // CJK extension A
            or >= 0x4E00 and <= 0x9FFF             // CJK unified
            or >= 0xAC00 and <= 0xD7AF             // Hangul syllables
            or >= 0x0E00 and <= 0x0E7F             // Thai
            or >= 0x20000 and <= 0x2FFFF;          // CJK extensions B+
    }
}

/// <summary>Turns recogniser segments into display cues: wrapped to the line width and split when they exceed the line budget.</summary>
public static class SegmentSplitter
{
    /// <summary>
    /// Each returned cue holds at most <c>MaxLines</c> lines of at most <c>MaxCharsPerLine</c> characters, joined with '\n'.
    /// A segment that needs several cues has its time divided in proportion to the text each cue carries; cues of one segment
    /// are contiguous and the last one ends exactly where the segment ends.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Split(IEnumerable<TranscriptSegment> segments, SubtitleOptions? options = null)
    {
        var o = (options ?? new SubtitleOptions()).Validated();
        var cues = new List<TranscriptSegment>();
        foreach (var seg in segments)
        {
            var lines = LineWrapper.Wrap(TextNormalizer.Normalize(seg.Text), o.MaxCharsPerLine);
            if (lines.Count == 0) continue;

            var groups = lines.Chunk(o.MaxLines).ToList();
            if (groups.Count == 1)
            {
                cues.Add(new TranscriptSegment(seg.Start, seg.End, string.Join('\n', groups[0])));
                continue;
            }

            var weights = groups.Select(g => (double)g.Sum(LineWrapper.Length)).ToList();
            var total = weights.Sum();
            var duration = seg.Duration;
            var cursor = seg.Start;
            var consumed = 0.0;
            for (var i = 0; i < groups.Count; i++)
            {
                consumed += weights[i];
                var end = i == groups.Count - 1
                    ? seg.End
                    : seg.Start + TimeSpan.FromTicks((long)(duration.Ticks * (consumed / total)));
                cues.Add(new TranscriptSegment(cursor, end, string.Join('\n', groups[i])));
                cursor = end;
            }
        }
        return cues;
    }
}
