using System.Text;

namespace DropFlow.AI.Translation.Rendering;

/// <summary>
/// Turns logical Arabic text into the glyph forms a plain left-to-right drawing routine needs: every letter is replaced by its isolated,
/// final, initial or medial presentation form (U+FE70..U+FEFF) so neighbouring letters connect, lam+alef becomes one ligature, and the whole line
/// is reordered into visual order (right-to-left runs reversed, numbers and Latin words kept readable, brackets mirrored).
/// <para>
/// ImageMagick draws glyph by glyph without any shaping, so this is what makes translated Arabic look right in
/// <see cref="TextOverlayRenderer"/>. It covers the Arabic alphabet, the common diacritics and the usual punctuation, which is what the
/// translation model produces; it is not a general-purpose text layout engine.
/// </para>
/// </summary>
public static class ArabicShaper
{
    // base letter -> (isolated, final, initial, medial); 0 means the form does not exist (right-joining letters have no initial/medial).
    private static readonly Dictionary<char, (char Iso, char Fin, char Ini, char Med)> Forms = new()
    {
        ['ء'] = ('ﺀ', '\0', '\0', '\0'),
        ['آ'] = ('ﺁ', 'ﺂ', '\0', '\0'),
        ['أ'] = ('ﺃ', 'ﺄ', '\0', '\0'),
        ['ؤ'] = ('ﺅ', 'ﺆ', '\0', '\0'),
        ['إ'] = ('ﺇ', 'ﺈ', '\0', '\0'),
        ['ئ'] = ('ﺉ', 'ﺊ', 'ﺋ', 'ﺌ'),
        ['ا'] = ('ﺍ', 'ﺎ', '\0', '\0'),
        ['ب'] = ('ﺏ', 'ﺐ', 'ﺑ', 'ﺒ'),
        ['ة'] = ('ﺓ', 'ﺔ', '\0', '\0'),
        ['ت'] = ('ﺕ', 'ﺖ', 'ﺗ', 'ﺘ'),
        ['ث'] = ('ﺙ', 'ﺚ', 'ﺛ', 'ﺜ'),
        ['ج'] = ('ﺝ', 'ﺞ', 'ﺟ', 'ﺠ'),
        ['ح'] = ('ﺡ', 'ﺢ', 'ﺣ', 'ﺤ'),
        ['خ'] = ('ﺥ', 'ﺦ', 'ﺧ', 'ﺨ'),
        ['د'] = ('ﺩ', 'ﺪ', '\0', '\0'),
        ['ذ'] = ('ﺫ', 'ﺬ', '\0', '\0'),
        ['ر'] = ('ﺭ', 'ﺮ', '\0', '\0'),
        ['ز'] = ('ﺯ', 'ﺰ', '\0', '\0'),
        ['س'] = ('ﺱ', 'ﺲ', 'ﺳ', 'ﺴ'),
        ['ش'] = ('ﺵ', 'ﺶ', 'ﺷ', 'ﺸ'),
        ['ص'] = ('ﺹ', 'ﺺ', 'ﺻ', 'ﺼ'),
        ['ض'] = ('ﺽ', 'ﺾ', 'ﺿ', 'ﻀ'),
        ['ط'] = ('ﻁ', 'ﻂ', 'ﻃ', 'ﻄ'),
        ['ظ'] = ('ﻅ', 'ﻆ', 'ﻇ', 'ﻈ'),
        ['ع'] = ('ﻉ', 'ﻊ', 'ﻋ', 'ﻌ'),
        ['غ'] = ('ﻍ', 'ﻎ', 'ﻏ', 'ﻐ'),
        ['ـ'] = ('ـ', 'ـ', 'ـ', 'ـ'),
        ['ف'] = ('ﻑ', 'ﻒ', 'ﻓ', 'ﻔ'),
        ['ق'] = ('ﻕ', 'ﻖ', 'ﻗ', 'ﻘ'),
        ['ك'] = ('ﻙ', 'ﻚ', 'ﻛ', 'ﻜ'),
        ['ل'] = ('ﻝ', 'ﻞ', 'ﻟ', 'ﻠ'),
        ['م'] = ('ﻡ', 'ﻢ', 'ﻣ', 'ﻤ'),
        ['ن'] = ('ﻥ', 'ﻦ', 'ﻧ', 'ﻨ'),
        ['ه'] = ('ﻩ', 'ﻪ', 'ﻫ', 'ﻬ'),
        ['و'] = ('ﻭ', 'ﻮ', '\0', '\0'),
        ['ى'] = ('ﻯ', 'ﻰ', '\0', '\0'),
        ['ي'] = ('ﻱ', 'ﻲ', 'ﻳ', 'ﻴ'),
    };

    // Persian/Urdu letters that sometimes appear in OCR or names: shaped like the Arabic letter they resemble.
    private static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['ک'] = 'ك', ['ی'] = 'ي', ['ھ'] = 'ه', ['ہ'] = 'ه', ['ە'] = 'ه',
    };

    private static readonly Dictionary<char, (char Iso, char Fin)> LamAlef = new()
    {
        ['آ'] = ('ﻵ', 'ﻶ'),
        ['أ'] = ('ﻷ', 'ﻸ'),
        ['إ'] = ('ﻹ', 'ﻺ'),
        ['ا'] = ('ﻻ', 'ﻼ'),
    };

    private static bool IsMark(char c) => c is >= 'ً' and <= 'ٟ' or 'ٰ' or >= 'ۖ' and <= 'ۭ';

    /// <summary>Letters that connect to the letter after them (dual-joining).</summary>
    private static bool JoinsNext(char c) => Forms.TryGetValue(c, out var f) && f.Ini != '\0';

    /// <summary>Letters that connect to the letter before them (dual- and right-joining).</summary>
    private static bool JoinsPrevious(char c) => Forms.TryGetValue(c, out var f) && f.Fin != '\0';

    /// <summary>True when the text has at least one Arabic letter (the signal to draw it right to left).</summary>
    public static bool HasArabic(string text) => text.Any(ScriptDetector.IsArabicLetter);

    /// <summary>Shapes the letters of <paramref name="logical"/> without reordering (still in logical order).</summary>
    public static string Shape(string logical)
    {
        var text = new StringBuilder(logical.Length);
        foreach (var c in logical) text.Append(Lookalikes.TryGetValue(c, out var l) ? l : c);
        var s = text.ToString();

        var result = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!Forms.TryGetValue(c, out var f)) { result.Append(c); continue; }

            var previous = PreviousBase(s, i);
            var joinPrev = previous is { } p && JoinsNext(p) && JoinsPrevious(c);

            // lam + alef (marks between them are allowed to be skipped)
            if (c == 'ل')
            {
                var j = i + 1;
                while (j < s.Length && IsMark(s[j])) j++;
                if (j < s.Length && LamAlef.TryGetValue(s[j], out var lig))
                {
                    result.Append(joinPrev ? lig.Fin : lig.Iso);
                    for (var k = i + 1; k < j; k++) result.Append(s[k]);
                    i = j;
                    continue;
                }
            }

            var next = NextBase(s, i);
            var joinNext = next is { } n && JoinsNext(c) && JoinsPrevious(n);
            var glyph = (joinPrev, joinNext) switch
            {
                (true, true) => f.Med != '\0' ? f.Med : f.Fin,
                (true, false) => f.Fin != '\0' ? f.Fin : f.Iso,
                (false, true) => f.Ini != '\0' ? f.Ini : f.Iso,
                _ => f.Iso,
            };
            result.Append(glyph);
        }
        return result.ToString();
    }

    private static char? PreviousBase(string s, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (IsMark(s[i])) continue;
            return Forms.ContainsKey(s[i]) ? s[i] : null;
        }
        return null;
    }

    private static char? NextBase(string s, int index)
    {
        for (var i = index + 1; i < s.Length; i++)
        {
            if (IsMark(s[i])) continue;
            return Forms.ContainsKey(s[i]) ? s[i] : null;
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------------ bidi

    private enum Dir { Rtl, Ltr, Neutral }

    /// <summary>
    /// Reorders one line of logical text for drawing left to right in a right-to-left paragraph: Arabic runs are reversed, numbers and Latin
    /// words stay readable, neutral characters between two right-to-left runs join them, brackets are mirrored.
    /// </summary>
    public static string ToVisual(string line)
    {
        if (line.Length == 0) return line;
        var chars = line.ToCharArray();
        var dirs = new Dir[chars.Length];
        for (var i = 0; i < chars.Length; i++) dirs[i] = Classify(chars[i]);

        // W4/W5: a single "." "," ":" "/" between digits, and a percent sign after a digit, belong to the number.
        for (var i = 1; i < chars.Length - 1; i++)
            if (chars[i] is '.' or ',' or ':' or '/' && char.IsAsciiDigit(chars[i - 1]) && char.IsAsciiDigit(chars[i + 1])) dirs[i] = Dir.Ltr;
        for (var i = 1; i < chars.Length; i++)
            if (chars[i] is '%' && char.IsAsciiDigit(chars[i - 1])) dirs[i] = Dir.Ltr;

        // Neutrals take the direction of the text around them when both sides agree; otherwise the paragraph direction (right to left).
        // Digits behave as right-to-left text for this decision (rule N1) even though they are drawn left to right.
        for (var i = 0; i < chars.Length; i++)
        {
            if (dirs[i] != Dir.Neutral) continue;
            var end = i;
            while (end < chars.Length && dirs[end] == Dir.Neutral) end++;
            var before = i > 0 ? InfluenceOf(chars[i - 1], dirs[i - 1]) : Dir.Rtl;
            var after = end < chars.Length ? InfluenceOf(chars[end], dirs[end]) : Dir.Rtl;
            var resolved = before == after ? before : Dir.Rtl;
            for (var k = i; k < end; k++) dirs[k] = resolved;
            i = end - 1;
        }

        // Runs of equal direction, grouping a base letter with the marks after it.
        var runs = new List<(Dir Dir, string Text)>();
        var start = 0;
        for (var i = 1; i <= chars.Length; i++)
        {
            if (i < chars.Length && (dirs[i] == dirs[start] || IsMark(chars[i]))) continue;
            runs.Add((dirs[start], new string(chars, start, i - start)));
            start = i;
        }

        var visual = new StringBuilder(line.Length);
        for (var r = runs.Count - 1; r >= 0; r--)          // the first logical run is the rightmost one
        {
            var (dir, text) = runs[r];
            if (dir == Dir.Ltr) visual.Append(text);
            else visual.Append(ReverseClusters(text));
        }
        return visual.ToString();
    }

    private static Dir InfluenceOf(char c, Dir resolved) => char.IsAsciiDigit(c) ? Dir.Rtl : resolved;

    private static Dir Classify(char c)
    {
        if (ScriptDetector.IsArabicLetter(c) || IsMark(c) || c is '،' or '؛' or '؟' or '٫' or '٬' or 'ـ') return Dir.Rtl;
        if (char.IsAsciiDigit(c)) return Dir.Ltr;
        if (c is >= '٠' and <= '٩' or >= '۰' and <= '۹') return Dir.Ltr;       // Arabic-Indic digits are drawn left to right
        if (char.IsLetter(c)) return Dir.Ltr;
        return Dir.Neutral;
    }

    /// <summary>Reverses a right-to-left run, keeping each mark behind the letter it belongs to and mirroring brackets.</summary>
    private static string ReverseClusters(string text)
    {
        var clusters = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (IsMark(c) && sb.Length > 0) { sb.Append(c); continue; }
            if (sb.Length > 0) clusters.Add(sb.ToString());
            sb.Clear().Append(Mirror(c));
        }
        if (sb.Length > 0) clusters.Add(sb.ToString());
        clusters.Reverse();
        return string.Concat(clusters);
    }

    private static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<', '«' => '»', '»' => '«',
        _ => c,
    };

    /// <summary>Shaped and reordered text ready for a left-to-right glyph drawer. Text without Arabic letters is returned as it is.</summary>
    public static string Prepare(string logicalLine) => HasArabic(logicalLine) ? ToVisual(Shape(logicalLine)) : logicalLine;
}
