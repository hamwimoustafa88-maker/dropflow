using System.Text;
using DropFlow.AI.Speech;

namespace DropFlow.AI.Ocr;

/// <summary>
/// Turns word boxes into text in the right reading order, independent of the order an engine happened to list them in.
/// <para>
/// Boxes are positions on the page, so left-to-right position is the only fact we can trust. For a right-to-left line
/// (Arabic, Hebrew...) the reading order is the unicode bidi rule applied to word runs: runs go right to left, the words of a
/// right-to-left run go right to left, and a run of left-to-right words (English names, numbers) keeps its left-to-right order.
/// </para>
/// </summary>
public static class OcrOrdering
{
    private enum Dir { Ltr, Rtl, Neutral }

    /// <summary>Groups loose words into lines by vertical overlap, top to bottom, and puts each line in reading order.</summary>
    public static IReadOnlyList<OcrLine> GroupIntoLines(IEnumerable<OcrWord> words)
    {
        var sorted = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Bounds.CenterY).ToList();
        var groups = new List<List<OcrWord>>();
        foreach (var word in sorted)
        {
            var group = groups.FirstOrDefault(g =>
            {
                var bounds = OcrRect.Union(g.Select(x => x.Bounds));
                return word.Bounds.VerticalOverlap(bounds) >= 0.5;
            });
            if (group is null) groups.Add([word]);
            else group.Add(word);
        }
        return groups
            .Select(g => BuildLine(g))
            .OrderBy(l => l.Bounds.CenterY)
            .ToList();
    }

    /// <summary>Builds one line from its words (in any order): sorts them by position, decides the direction and returns them in reading order.</summary>
    public static OcrLine BuildLine(IEnumerable<OcrWord> words)
    {
        var visual = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Bounds.CenterX).ToList();
        if (visual.Count == 0) return new OcrLine("", [], default, false);

        var bounds = OcrRect.Union(visual.Select(w => w.Bounds));
        var rtl = RtlText.IsRightToLeft(string.Join(' ', visual.Select(w => w.Text)));
        var logical = ToLogicalOrder(visual, rtl);
        return new OcrLine(JoinWords(logical), logical, bounds, rtl);
    }

    /// <summary>
    /// <paramref name="visual"/> lists the words left to right as they appear. Returns them in reading order for a line whose base
    /// direction is <paramref name="baseIsRtl"/>.
    /// </summary>
    public static IReadOnlyList<OcrWord> ToLogicalOrder(IReadOnlyList<OcrWord> visual, bool baseIsRtl)
    {
        if (visual.Count <= 1) return visual;

        var dirs = visual.Select(w => Classify(w.Text)).ToArray();
        ResolveNeutrals(dirs, baseIsRtl ? Dir.Rtl : Dir.Ltr);

        // Split into runs of equal direction.
        var runs = new List<(Dir Dir, List<OcrWord> Words)>();
        for (var i = 0; i < visual.Count; i++)
        {
            if (runs.Count > 0 && runs[^1].Dir == dirs[i]) runs[^1].Words.Add(visual[i]);
            else runs.Add((dirs[i], [visual[i]]));
        }

        // Right-to-left runs read right to left inside; the order of the runs themselves follows the base direction.
        var ordered = baseIsRtl ? Enumerable.Reverse(runs) : runs;
        var result = new List<OcrWord>(visual.Count);
        foreach (var (dir, runWords) in ordered)
        {
            if (dir == Dir.Rtl) result.AddRange(Enumerable.Reverse(runWords));
            else result.AddRange(runWords);
        }
        return result;
    }

    /// <summary>Joins words with spaces, except between two characters of an unspaced script (Chinese, Japanese, Thai).</summary>
    public static string JoinWords(IReadOnlyList<OcrWord> words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var text = words[i].Text;
            if (i > 0 && !(EndsUnspaced(words[i - 1].Text) && StartsUnspaced(text))) sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static Dir Classify(string word)
    {
        var (rtl, ltr) = RtlText.CountStrong(word);
        if (rtl > 0 && rtl >= ltr) return Dir.Rtl;
        if (ltr > 0) return Dir.Ltr;
        // No letters: a number keeps left-to-right digit order; bare punctuation takes its neighbours' direction.
        return word.Any(char.IsDigit) ? Dir.Ltr : Dir.Neutral;
    }

    private static void ResolveNeutrals(Dir[] dirs, Dir baseDir)
    {
        for (var i = 0; i < dirs.Length; i++)
        {
            if (dirs[i] != Dir.Neutral) continue;
            var left = i - 1;
            while (left >= 0 && dirs[left] == Dir.Neutral) left--;
            var right = i + 1;
            while (right < dirs.Length && dirs[right] == Dir.Neutral) right++;
            var l = left >= 0 ? dirs[left] : baseDir;
            var r = right < dirs.Length ? dirs[right] : baseDir;
            dirs[i] = l == r ? l : baseDir;
        }
    }

    private static bool StartsUnspaced(string s) => s.Length > 0 && IsUnspaced(char.ConvertToUtf32(s, 0));

    private static bool EndsUnspaced(string s)
    {
        if (s.Length == 0) return false;
        var i = s.Length - 1;
        if (i > 0 && char.IsLowSurrogate(s[i])) i--;
        return IsUnspaced(char.ConvertToUtf32(s, i));
    }

    private static bool IsUnspaced(int cp) =>
        cp is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF
            or >= 0x0E00 and <= 0x0E7F or >= 0x20000 and <= 0x2FFFF;
}
