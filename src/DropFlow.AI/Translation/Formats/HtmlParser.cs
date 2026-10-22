using System.Net;
using System.Text.RegularExpressions;

namespace DropFlow.AI.Translation.Formats;

/// <summary>
/// HTML: the text between tags is translated; tags, attributes, comments, scripts, styles, code and preformatted blocks are copied unchanged.
/// (Attribute texts such as <c>alt</c> and <c>title</c> are not translated.) Entities are decoded for the model and the answer is re-escaped.
/// </summary>
internal static partial class HtmlParser
{
    // A comment, a doctype/declaration, or a tag (attribute values may contain ">" inside quotes).
    [GeneratedRegex(@"<!--.*?-->|<![^>]*>|<\?.*?\?>|</?[A-Za-z][A-Za-z0-9:-]*(?:""[^""]*""|'[^']*'|[^'"">])*>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    [GeneratedRegex(@"^<(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9:-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TagName();

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "pre", "code", "kbd", "samp", "textarea", "svg", "math", "noscript", "template",
    };

    public static TranslationTemplate Parse(string html)
    {
        var parts = new List<Part>();
        var position = 0;
        string? raw = null;                  // name of the element whose content is copied as it is
        foreach (Match m in Markup().Matches(html))
        {
            AddText(html[position..m.Index], raw is not null, parts);
            parts.Add(Part.Keep(m.Value));
            position = m.Index + m.Length;

            if (TagName().Match(m.Value) is { Success: true } tag)
            {
                var name = tag.Groups["name"].Value;
                var closing = tag.Groups["close"].Success;
                var selfClosing = m.Value.EndsWith("/>", StringComparison.Ordinal);
                if (raw is null && !closing && !selfClosing && RawTextElements.Contains(name)) raw = name;
                else if (raw is not null && closing && name.Equals(raw, StringComparison.OrdinalIgnoreCase)) raw = null;
            }
        }
        AddText(html[position..], raw is not null, parts);
        return new TranslationTemplate(parts);
    }

    private static void AddText(string text, bool keep, List<Part> parts)
    {
        if (text.Length == 0) return;
        if (keep || text.Trim().Length == 0) { parts.Add(Part.Keep(text)); return; }
        // Keep the white space around the text outside the unit: it carries the formatting of the source.
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        if (lead > 0) parts.Add(Part.Keep(text[..lead]));
        var core = text[lead..(text.Length - trail)];
        // Line breaks inside a text node are just white space in HTML.
        var decoded = WebUtility.HtmlDecode(Regex.Replace(core, @"\s+", " "));
        parts.Add(Part.Tr(decoded, htmlEscape: true));
        if (trail > 0) parts.Add(Part.Keep(text[(text.Length - trail)..]));
    }

    [GeneratedRegex(@"(<html\b)([^>]*)(>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s(?:lang|xml:lang|dir)\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LangAttribute();

    [GeneratedRegex(@"(<meta[^>]*charset\s*=\s*[""']?)[^""'>\s;/]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharsetMeta();

    /// <summary>Sets <c>lang</c> and <c>dir</c> on the &lt;html&gt; element for the target language and declares UTF-8 (the output is always UTF-8).</summary>
    public static string FixDocument(string html, TranslationDirection direction)
    {
        var toArabic = direction == TranslationDirection.EnglishToArabic;
        html = HtmlTag().Replace(html, m =>
            m.Groups[1].Value + LangAttribute().Replace(m.Groups[2].Value, "") + (toArabic ? " lang=\"ar\" dir=\"rtl\"" : " lang=\"en\" dir=\"ltr\"") + m.Groups[3].Value, 1);
        return CharsetMeta().Replace(html, "$1utf-8");
    }
}
