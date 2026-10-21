using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DropFlow.Engines.Media.Subtitles;

/// <summary>Conversions between each format's inline markup and the canonical text model (see <see cref="SubtitleCue"/>).</summary>
internal static partial class SubtitleText
{
    private static readonly HashSet<string> Kept = ["i", "b", "u", "s"];

    /// <summary>
    /// Reduces SRT/VTT payload markup to the canonical form: keeps i/b/u/s, turns &lt;br&gt; into a line break, drops every other tag
    /// (font, c, v, lang, ruby, karaoke timestamps) and ASS-style <c>{\an8}</c> blocks, then decodes HTML entities.
    /// </summary>
    public static string FromHtmlish(string raw, bool decodeEntities)
    {
        var s = AssOverride().Replace(raw, "");
        s = VttTimestamp().Replace(s, "");
        s = Tag().Replace(s, m =>
        {
            var name = m.Groups["name"].Value.ToLowerInvariant();
            var closing = m.Groups["close"].Success;
            if (name == "br") return "\n";
            if (!Kept.Contains(name)) return "";
            return closing ? $"</{name}>" : $"<{name}>";
        });
        if (decodeEntities) s = WebUtility.HtmlDecode(s);
        return s;
    }

    /// <summary>
    /// ASS dialogue text to canonical: <c>\N</c> is a line break, <c>\n</c> and <c>\h</c> become spaces, \i \b \u \s toggles become tags,
    /// vector drawings (<c>\p1</c>) are removed, every other override is dropped. Open tags are closed at the end of the cue.
    /// </summary>
    public static string FromAss(string raw)
    {
        var sb = new StringBuilder();
        var open = new List<char>(); // currently open tags, outermost first
        var drawing = false;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '{')
            {
                var close = raw.IndexOf('}', i + 1);
                if (close < 0) { i = raw.Length; break; } // unterminated override: ignore the rest of the block
                ApplyOverrides(raw.Substring(i + 1, close - i - 1), sb, open, ref drawing);
                i = close + 1;
                continue;
            }
            if (c == '\\' && i + 1 < raw.Length)
            {
                var n = raw[i + 1];
                if (n == 'N') { if (!drawing) sb.Append('\n'); i += 2; continue; }
                if (n is 'n' or 'h') { if (!drawing) sb.Append(' '); i += 2; continue; }
            }
            if (!drawing) sb.Append(c);
            i++;
        }
        for (var k = open.Count - 1; k >= 0; k--) sb.Append($"</{open[k]}>");
        return sb.ToString();
    }

    private static void ApplyOverrides(string block, StringBuilder sb, List<char> open, ref bool drawing)
    {
        foreach (Match m in OverrideTag().Matches(block))
        {
            var name = m.Groups["name"].Value;
            var value = m.Groups["value"].Value;
            if (name == "p")
            {
                if (value.Length == 0) continue; // \pos(...) and similar tags that merely start with p
                drawing = int.TryParse(value, out var level) && level > 0;
                continue;
            }
            if (name == "r")
            {
                for (var k = open.Count - 1; k >= 0; k--)
                    if (!drawing) sb.Append($"</{open[k]}>");
                open.Clear();
                continue;
            }
            var key = name[0];
            if (name.Length != 1 || value.Length == 0) continue;
            var on = int.TryParse(value, out var v) && v > 0;
            var isOpen = open.Contains(key);
            if (on == isOpen) continue;
            if (drawing) { if (on) open.Add(key); else open.Remove(key); continue; }

            if (on)
            {
                open.Add(key);
                sb.Append($"<{key}>");
            }
            else
            {
                // Keep the tags properly nested: close everything opened after this one, close it, then reopen the rest.
                var index = open.LastIndexOf(key);
                var above = open.Skip(index + 1).ToList();
                for (var k = above.Count - 1; k >= 0; k--) sb.Append($"</{above[k]}>");
                sb.Append($"</{key}>");
                foreach (var t in above) sb.Append($"<{t}>");
                open.RemoveAt(index);
            }
        }
    }

    /// <summary>Canonical text with its i/b/u/s tags removed. Used for plain-text export.</summary>
    public static string ToPlain(string canonical) => CanonicalTag().Replace(canonical, "");

    /// <summary>Canonical text to WebVTT payload: escapes &amp; and &lt; (except our tags) and defuses the cue-end arrow.</summary>
    public static string ToVtt(string canonical)
    {
        var sb = new StringBuilder();
        var pos = 0;
        foreach (Match m in CanonicalTag().Matches(canonical))
        {
            sb.Append(EscapeVttSegment(canonical[pos..m.Index]));
            sb.Append(m.Value);
            pos = m.Index + m.Length;
        }
        sb.Append(EscapeVttSegment(canonical[pos..]));
        return sb.ToString();
    }

    private static string EscapeVttSegment(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace("-->", "--&gt;", StringComparison.Ordinal);

    /// <summary>Canonical text to ASS dialogue text.</summary>
    public static string ToAss(string canonical)
    {
        // ASS has no way to write a literal brace: use parentheses so the text is not mistaken for an override block.
        var s = canonical.Replace('{', '(').Replace('}', ')');
        s = CanonicalTag().Replace(s, m => m.Value[1] == '/' ? $"{{\\{m.Value[2]}0}}" : $"{{\\{m.Value[1]}1}}");
        return s.Replace("\r\n", "\\N", StringComparison.Ordinal).Replace("\n", "\\N", StringComparison.Ordinal);
    }

    /// <summary>Collapses blank lines (they would end the cue in SRT/VTT) and trims each line.</summary>
    public static string TidyLines(string s)
    {
        var lines = s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"<(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9]*)(?<rest>[^<>]*)>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"<\d{1,3}:\d{2}(?::\d{2})?[.,]\d{1,3}>")]
    private static partial Regex VttTimestamp();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssOverride();

    [GeneratedRegex(@"</?[ibus]>")]
    private static partial Regex CanonicalTag();

    // \i1  \b0  \u1  \s0  \r  \rStyle  \p1  (the value is the digits that follow)
    [GeneratedRegex(@"\\(?<name>[ibusp]|r)(?<value>\d*)")]
    private static partial Regex OverrideTag();
}
