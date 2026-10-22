using System.Text;

namespace DropFlow.AI.Translation.Formats;

/// <summary>
/// One part of a file being translated. Parts that are not translated (timestamps, Markdown syntax, tags, code, line breaks) are copied
/// to the output exactly; the others are replaced by their translation.
/// </summary>
/// <param name="Text">What the part contains in the source file (already decoded for HTML).</param>
/// <param name="Translate">True when the text goes to the translator.</param>
/// <param name="WrapWidth">When above zero, the translation is wrapped to this many characters (a hard-wrapped paragraph keeps its look).</param>
/// <param name="ExactLines">When above one, the translation is spread over this many lines (a subtitle keeps its line count).</param>
/// <param name="LinePrefix">Written in front of every line of a wrapped translation except the first (the "&gt; " of a quote).</param>
/// <param name="FirstLineOffset">Characters already on the first line before this part (a list marker), counted against <paramref name="WrapWidth"/>.</param>
/// <param name="HtmlEscape">The translation is HTML-escaped when written (the part's text was HTML-decoded).</param>
internal sealed record Part(string Text, bool Translate, int WrapWidth = 0, int ExactLines = 0, string LinePrefix = "", int FirstLineOffset = 0, bool HtmlEscape = false)
{
    public static Part Keep(string text) => new(text, false);

    public static Part Tr(string text, int wrapWidth = 0, int exactLines = 0, string linePrefix = "", int firstLineOffset = 0, bool htmlEscape = false) =>
        new(text, true, wrapWidth, exactLines, linePrefix, firstLineOffset, htmlEscape);
}

/// <summary>A file cut into kept and translatable parts. Rendering with the translations gives the output file's text (lines end with "\n").</summary>
internal sealed class TranslationTemplate(IReadOnlyList<Part> parts)
{
    public IReadOnlyList<Part> Parts { get; } = parts;

    /// <summary>The strings to translate, in file order.</summary>
    public IReadOnlyList<string> Units { get; } = [.. parts.Where(p => p.Translate).Select(p => p.Text)];

    /// <summary>Output text built from <paramref name="translations"/> (one per unit, same order).</summary>
    public string Render(IReadOnlyList<string> translations)
    {
        if (translations.Count != Units.Count) throw new ArgumentException("One translation per unit is required.", nameof(translations));
        var sb = new StringBuilder();
        var next = 0;
        foreach (var part in Parts)
        {
            if (!part.Translate) { sb.Append(part.Text); continue; }
            var t = translations[next++];
            if (part.ExactLines > 1) t = TextWrap.Balanced(t, part.ExactLines, part.LinePrefix);
            else if (part.WrapWidth > 0) t = TextWrap.Greedy(t, part.WrapWidth, part.LinePrefix, part.FirstLineOffset);
            if (part.HtmlEscape) t = t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            sb.Append(t);
        }
        return sb.ToString();
    }
}

internal static class TextWrap
{
    /// <summary>
    /// Breaks <paramref name="text"/> at spaces into lines of at most <paramref name="width"/> characters (a longer word keeps its own line).
    /// Every line after the first starts with <paramref name="linePrefix"/>; the first line is assumed to have <paramref name="firstLineOffset"/> characters already.
    /// </summary>
    public static string Greedy(string text, int width, string linePrefix, int firstLineOffset = 0)
    {
        var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return text;
        var sb = new StringBuilder();
        var length = firstLineOffset;
        var lineHasWord = false;
        foreach (var w in words)
        {
            if (lineHasWord && length + 1 + w.Length > width)
            {
                sb.Append('\n').Append(linePrefix);
                length = linePrefix.Length;
                lineHasWord = false;
            }
            if (lineHasWord) { sb.Append(' '); length++; }
            sb.Append(w);
            length += w.Length;
            lineHasWord = true;
        }
        return sb.ToString();
    }

    /// <summary>Spreads the words over exactly <paramref name="lines"/> lines of similar length (fewer when there are fewer words).</summary>
    public static string Balanced(string text, int lines, string linePrefix)
    {
        var words = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        lines = Math.Min(lines, words.Length);
        if (lines <= 1) return string.Join(' ', words);

        // Choose the break points that make the longest line as short as possible.
        var n = words.Length;
        var prefix = new int[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + words[i].Length + 1;
        int Width(int from, int to) => prefix[to] - prefix[from] - 1;       // words [from, to)

        var best = new int[lines + 1, n + 1];
        var cut = new int[lines + 1, n + 1];
        for (var j = 0; j <= n; j++) best[1, j] = j == 0 ? int.MaxValue : Width(0, j);
        for (var l = 2; l <= lines; l++)
            for (var j = l; j <= n; j++)
            {
                best[l, j] = int.MaxValue;
                for (var k = l - 1; k < j; k++)
                {
                    var cost = Math.Max(best[l - 1, k], Width(k, j));
                    if (cost < best[l, j]) { best[l, j] = cost; cut[l, j] = k; }
                }
            }

        var breaks = new List<int>();
        var end = n;
        for (var l = lines; l >= 2; l--) { end = cut[l, end]; breaks.Add(end); }
        breaks.Reverse();
        var sb = new StringBuilder();
        var start = 0;
        foreach (var b in breaks.Append(n))
        {
            if (start > 0) sb.Append('\n').Append(linePrefix);
            sb.Append(string.Join(' ', words[start..b]));
            start = b;
        }
        return sb.ToString();
    }
}
