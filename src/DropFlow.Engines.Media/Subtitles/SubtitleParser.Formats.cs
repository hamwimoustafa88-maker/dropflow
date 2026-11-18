// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DropFlow.Engines.Media.Subtitles;

/// <summary>
/// Readers for the formats DropFlow only reads: SAMI (.smi), LRC lyrics, TTML/DFXP, MicroDVD (.sub, frame based; SubViewer 2 .sub
/// files are read through the SBV reader) and YouTube SBV. Each reader returns an empty document (no cues) for content that is not
/// its format, so <see cref="Parse"/> can try the next one.
/// </summary>
public static partial class SubtitleParser
{
    /// <summary>How long the last line of a file that never says when it ends (SAMI, LRC, MicroDVD with an empty end) stays on screen.</summary>
    internal static readonly TimeSpan LastCueDuration = TimeSpan.FromSeconds(3);

    /// <summary>The frame rate assumed for MicroDVD files that do not declare one (film transferred to NTSC video).</summary>
    internal const double DefaultMicroDvdFps = 23.976;

    // ------------------------------------------------------------------ SAMI

    /// <summary>
    /// SAMI: <c>&lt;SYNC Start=ms&gt;&lt;P Class=X&gt;text</c>. A caption lasts until the next SYNC of its class (an empty or
    /// <c>&amp;nbsp;</c> one clears the screen). Line breaks in the source are spaces (HTML rules); <c>&lt;br&gt;</c> is a line break.
    /// A file with several languages (classes) yields the first one used in the body; the others are named in a warning.
    /// </summary>
    public static SubtitleDocument ParseSami(string normalizedText, string newLine)
    {
        var warnings = new List<string>();
        if (SamiSync().Matches(normalizedText).Count == 0) return new SubtitleDocument([], SubtitleFormat.Smi, newLine, warnings);

        var text = HtmlComment().Replace(normalizedText, "");
        var bodyAt = text.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyAt >= 0) text = text[bodyAt..];
        var title = SamiTitle().Match(normalizedText) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups["t"].Value).Trim() : null;

        // (class, start, end?, text) in file order
        var events = new List<(string Class, long Start, long? End, string Text)>();
        var syncs = SamiSync().Matches(text);
        for (var i = 0; i < syncs.Count; i++)
        {
            var sync = syncs[i];
            if (!long.TryParse(sync.Groups["start"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) continue;
            long? end = sync.Groups["end"].Success && long.TryParse(sync.Groups["end"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : null;
            var contentStart = sync.Index + sync.Length;
            var contentEnd = i + 1 < syncs.Count ? syncs[i + 1].Index : text.Length;
            var content = text[contentStart..contentEnd];
            content = SamiBodyEnd().Replace(content, "");

            var paragraphs = SamiParagraph().Matches(content);
            if (paragraphs.Count == 0)
            {
                events.Add(("", start, end, SamiText(content)));
                continue;
            }
            for (var p = 0; p < paragraphs.Count; p++)
            {
                var from = paragraphs[p].Index + paragraphs[p].Length;
                var to = p + 1 < paragraphs.Count ? paragraphs[p + 1].Index : content.Length;
                var cls = paragraphs[p].Groups["class"].Success ? paragraphs[p].Groups["class"].Value : "";
                events.Add((cls, start, end, SamiText(content[from..to])));
            }
        }

        var classes = events.Where(x => x.Text.Length > 0).Select(x => x.Class).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (classes.Count == 0) return new SubtitleDocument([], SubtitleFormat.Smi, newLine, warnings, title);
        var chosen = classes[0];
        if (classes.Count > 1)
            warnings.Add($"The file has captions in {classes.Count} languages; only the first ({chosen}) was converted. Not converted: {string.Join(", ", classes.Skip(1))}.");

        var mine = events.Where(x => string.Equals(x.Class, chosen, StringComparison.OrdinalIgnoreCase))
            .Select((x, i) => (x, i)).OrderBy(p => p.x.Start).ThenBy(p => p.i).Select(p => p.x).ToList();
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < mine.Count; i++)
        {
            var (_, start, end, body) = mine[i];
            if (body.Length == 0) continue;
            var next = mine.Skip(i + 1).Select(x => (long?)x.Start).FirstOrDefault(s => s > start);
            var stop = end is { } explicitEnd && explicitEnd > start ? explicitEnd : next ?? start + (long)LastCueDuration.TotalMilliseconds;
            cues.Add(new SubtitleCue(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(stop), body));
        }
        return new SubtitleDocument(cues, SubtitleFormat.Smi, newLine, warnings, title);
    }

    private static string SamiText(string html)
    {
        var collapsed = Whitespace().Replace(html, " ");
        var text = SubtitleText.FromHtmlish(collapsed, decodeEntities: true).Replace(' ', ' ');
        return SubtitleText.TidyLines(text);
    }

    // ------------------------------------------------------------------ LRC

    /// <summary>
    /// LRC lyrics: <c>[mm:ss.xx]line</c>, several time tags may share one line, <c>[offset:±ms]</c> shifts everything (a positive
    /// offset shows the lyrics earlier), enhanced word times <c>&lt;mm:ss.xx&gt;</c> are removed. A line lasts until the next time tag;
    /// an empty line only ends the one before it. The last line gets <see cref="LastCueDuration"/>.
    /// </summary>
    public static SubtitleDocument ParseLrc(string normalizedText, string newLine)
    {
        var warnings = new List<string>();
        var events = new List<(long Ms, string Text)>();
        long offset = 0;
        string? title = null;

        foreach (var raw in normalizedText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var tags = LrcTimeTags().Match(line);
            if (tags.Success)
            {
                var body = line[tags.Length..];
                body = SubtitleText.TidyLines(SubtitleText.FromHtmlish(body, decodeEntities: false));
                foreach (Capture c in tags.Groups["tag"].Captures)
                    if (TryLrcTime(c.Value, out var ms)) events.Add((ms, body));
                continue;
            }

            var meta = LrcMeta().Match(line);
            if (!meta.Success) continue;
            var key = meta.Groups["key"].Value.ToLowerInvariant();
            var value = meta.Groups["value"].Value.Trim();
            if (key == "offset" && long.TryParse(value.TrimStart('+'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o)) offset = o;
            else if (key == "ti" && value.Length > 0) title = value;
        }

        var ordered = events.Select((e, i) => (e, i)).OrderBy(p => p.e.Ms).ThenBy(p => p.i).Select(p => p.e).ToList();
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (start, text) = ordered[i];
            if (text.Length == 0) continue;
            var next = ordered.Skip(i + 1).Select(x => (long?)x.Ms).FirstOrDefault(s => s > start);
            var end = next ?? start + (long)LastCueDuration.TotalMilliseconds;
            cues.Add(new SubtitleCue(Shift(start), Shift(end), text));
        }
        return new SubtitleDocument(cues, SubtitleFormat.Lrc, newLine, warnings, title);

        TimeSpan Shift(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms - offset));
    }

    /// <summary>"mm:ss", "mm:ss.xx", "mm:ss.xxx" or "mm:ss:xx" (some editors use a colon before the hundredths).</summary>
    private static bool TryLrcTime(string tag, out long ms)
    {
        ms = 0;
        var m = LrcTime().Match(tag);
        if (!m.Success) return false;
        var minutes = long.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (seconds > 59) return false;
        var fraction = m.Groups["f"].Success ? FractionToMs(m.Groups["f"].Value) : 0;
        ms = (minutes * 60 + seconds) * 1000 + fraction;
        return true;
    }

    // ------------------------------------------------------------------ YouTube SBV (and SubViewer 2)

    /// <summary>
    /// SBV: a <c>H:MM:SS.mmm,H:MM:SS.mmm</c> line, the text lines, a blank line. SubViewer 2 (<c>.sub</c>) has the same shape with
    /// centiseconds and <c>[br]</c> for a line break, so it is read here too; its <c>[INFORMATION]</c> header is ignored.
    /// </summary>
    public static SubtitleDocument ParseSbv(string normalizedText, string newLine)
    {
        var warnings = new List<string>();
        var cues = new List<SubtitleCue>();
        var lines = normalizedText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = SbvTiming().Match(lines[i]);
            if (!m.Success) continue;
            var body = new List<string>();
            var k = i + 1;
            for (; k < lines.Length && !string.IsNullOrWhiteSpace(lines[k]) && !SbvTiming().IsMatch(lines[k]); k++) body.Add(lines[k]);
            i = k - 1;

            var start = SbvTime(m, "1");
            var end = SbvTime(m, "2");
            var raw = string.Join("\n", body).Replace("[br]", "\n", StringComparison.OrdinalIgnoreCase);
            var text = SubtitleText.TidyLines(SubtitleText.FromHtmlish(raw, decodeEntities: false));
            if (text.Length == 0) continue;
            if (end < start)
            {
                warnings.Add($"A cue at {FormatForLog(start)} ends before it starts; its end was set to its start.");
                end = start;
            }
            cues.Add(new SubtitleCue(start, end, text));
        }
        return new SubtitleDocument(cues, SubtitleFormat.Sbv, newLine, warnings);
    }

    private static TimeSpan SbvTime(Match m, string n)
    {
        var h = long.Parse(m.Groups["h" + n].Value, CultureInfo.InvariantCulture);
        var mi = long.Parse(m.Groups["m" + n].Value, CultureInfo.InvariantCulture);
        var s = long.Parse(m.Groups["s" + n].Value, CultureInfo.InvariantCulture);
        return TimeSpan.FromMilliseconds(((h * 60 + mi) * 60 + s) * 1000 + FractionToMs(m.Groups["f" + n].Value));
    }

    // ------------------------------------------------------------------ MicroDVD

    /// <summary>
    /// MicroDVD: <c>{startFrame}{endFrame}text</c>, <c>|</c> between lines. Times are frames: a first line such as <c>{1}{1}25</c>
    /// declares the frame rate, otherwise <see cref="DefaultMicroDvdFps"/> is assumed (and said in a warning). Style codes: <c>{y:i}</c>
    /// (one line) and <c>{Y:i}</c> (every line) for italic/bold/underline/strike; a leading <c>/</c> also means italic; colour, font,
    /// size and position codes are dropped. An empty end frame lasts until the next cue.
    /// </summary>
    public static SubtitleDocument ParseMicroDvd(string normalizedText, string newLine)
    {
        var warnings = new List<string>();
        var raw = new List<(long Start, long? End, string Text)>();
        foreach (var line in normalizedText.Split('\n'))
        {
            var m = MicroDvdLine().Match(line.Trim());
            if (!m.Success) continue;
            var start = long.Parse(m.Groups["start"].Value, CultureInfo.InvariantCulture);
            long? end = m.Groups["end"].Value.Length > 0 ? long.Parse(m.Groups["end"].Value, CultureInfo.InvariantCulture) : null;
            raw.Add((start, end, m.Groups["text"].Value));
        }
        if (raw.Count == 0) return new SubtitleDocument([], SubtitleFormat.MicroDvd, newLine, warnings);

        var fps = DefaultMicroDvdFps;
        var declared = false;
        if (raw[0].Start <= 1 && raw[0].End is null or <= 1
            && double.TryParse(raw[0].Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate is > 1 and < 200)
        {
            fps = rate;
            declared = true;
            raw.RemoveAt(0);
        }
        if (!declared) warnings.Add($"The file does not say its frame rate; {DefaultMicroDvdFps.ToString(CultureInfo.InvariantCulture)} frames per second was assumed.");

        TimeSpan At(long frame) => TimeSpan.FromMilliseconds(Math.Round(frame * 1000.0 / fps, MidpointRounding.AwayFromZero));

        var cues = new List<SubtitleCue>();
        for (var i = 0; i < raw.Count; i++)
        {
            var (start, end, body) = raw[i];
            var text = SubtitleText.TidyLines(MicroDvdText(body));
            if (text.Length == 0) continue;
            var startTime = At(start);
            var endTime = end is { } e ? At(e) : i + 1 < raw.Count && raw[i + 1].Start > start ? At(raw[i + 1].Start) : startTime + LastCueDuration;
            if (endTime < startTime)
            {
                warnings.Add($"A cue at {FormatForLog(startTime)} ends before it starts; its end was set to its start.");
                endTime = startTime;
            }
            cues.Add(new SubtitleCue(startTime, endTime, text));
        }
        var ordered = cues.Select((c, i) => (c, i)).OrderBy(x => x.c.Start).ThenBy(x => x.i).Select(x => x.c).ToList();
        return new SubtitleDocument(ordered, SubtitleFormat.MicroDvd, newLine, warnings);
    }

    private static string MicroDvdText(string body)
    {
        var all = new HashSet<char>();
        foreach (Match code in MicroDvdCode().Matches(body))
            if (code.Groups["key"].Value == "Y") AddStyles(all, code.Groups["value"].Value);

        var lines = new List<string>();
        foreach (var part in body.Split('|'))
        {
            var styles = new HashSet<char>(all);
            foreach (Match code in MicroDvdCode().Matches(part))
                if (code.Groups["key"].Value == "y") AddStyles(styles, code.Groups["value"].Value);
            var line = MicroDvdCode().Replace(part, "").Trim();
            if (line.StartsWith('/'))
            {
                styles.Add('i');
                line = line[1..].TrimStart();
            }
            line = SubtitleText.FromHtmlish(line, decodeEntities: false);
            if (line.Length == 0) continue;
            var sb = new StringBuilder();
            foreach (var s in "bius".Where(styles.Contains)) sb.Append('<').Append(s).Append('>');
            sb.Append(line);
            foreach (var s in "suib".Where(styles.Contains)) sb.Append("</").Append(s).Append('>');
            lines.Add(sb.ToString());
        }
        return string.Join("\n", lines);

        static void AddStyles(HashSet<char> set, string value)
        {
            foreach (var v in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (v.Length == 1 && "bius".Contains(char.ToLowerInvariant(v[0]))) set.Add(char.ToLowerInvariant(v[0]));
        }
    }

    // ------------------------------------------------------------------ TTML / DFXP

    /// <summary>
    /// TTML and DFXP (any of their namespaces): every <c>&lt;p&gt;</c> with a time is a cue. Times are clock times, frames
    /// (<c>hh:mm:ss:ff</c>, <c>50f</c>), ticks (<c>40000000t</c>) or offsets (<c>1.5s</c>, <c>2m</c>, <c>750ms</c>), relative to the enclosing
    /// <c>body</c>/<c>div</c> begin; <c>dur</c> stands in for <c>end</c>. <c>&lt;br/&gt;</c> is a line break; italic, bold, underline and
    /// line-through come from <c>tts:</c> attributes on the element or on a style in the head. No DTD or external entity is processed.
    /// </summary>
    public static SubtitleDocument ParseTtml(string normalizedText, string newLine)
    {
        var warnings = new List<string>();
        var empty = new SubtitleDocument([], SubtitleFormat.Ttml, newLine, warnings);
        if (!normalizedText.Contains("<tt", StringComparison.Ordinal)) return empty;

        XDocument xml;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 1024 };
            using var reader = XmlReader.Create(new StringReader(normalizedText), settings);
            xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            warnings.Add($"The TTML file is not valid XML: {ex.Message}");
            return empty;
        }

        var root = xml.Root;
        if (root is null || root.Name.LocalName != "tt") return empty;

        var timing = TtmlTiming.From(root);
        var styles = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var style in root.Descendants().Where(e => e.Name.LocalName == "style"))
            if (XmlId(style) is { } id) styles[id] = style;

        var body = root.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
        if (body is null) return empty;
        var title = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();

        var cues = new List<(TimeSpan Start, TimeSpan? End, string Text)>();
        Walk(body, TimeSpan.Zero, null);

        var list = new List<SubtitleCue>();
        var sorted = cues.Select((c, i) => (c, i)).OrderBy(x => x.c.Start).ThenBy(x => x.i).Select(x => x.c).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var (start, end, text) = sorted[i];
            var stop = end ?? (i + 1 < sorted.Count && sorted[i + 1].Start > start ? sorted[i + 1].Start : start + LastCueDuration);
            if (stop < start)
            {
                warnings.Add($"A cue at {FormatForLog(start)} ends before it starts; its end was set to its start.");
                stop = start;
            }
            list.Add(new SubtitleCue(start, stop, text));
        }
        return new SubtitleDocument(list, SubtitleFormat.Ttml, newLine, warnings, string.IsNullOrEmpty(title) ? null : title);

        void Walk(XElement element, TimeSpan parentBegin, TimeSpan? parentEnd)
        {
            foreach (var child in element.Elements())
            {
                var name = child.Name.LocalName;
                if (name is not ("div" or "p")) continue;
                var begin = parentBegin + (timing.Parse(Attr(child, "begin")) ?? TimeSpan.Zero);
                TimeSpan? end = timing.Parse(Attr(child, "end")) is { } e ? parentBegin + e
                    : timing.Parse(Attr(child, "dur")) is { } d ? begin + d
                    : parentEnd;
                if (name == "div")
                {
                    Walk(child, begin, end);
                    continue;
                }
                var hasTime = Attr(child, "begin") is not null || Attr(child, "end") is not null || Attr(child, "dur") is not null
                              || parentBegin > TimeSpan.Zero || parentEnd is not null;
                if (!hasTime) continue;
                var text = SubtitleText.TidyLines(TtmlText(child, styles));
                if (text.Length > 0) cues.Add((begin, end, text));
            }
        }
    }

    private static string? Attr(XElement e, string localName) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName && (a.Name.Namespace == XNamespace.None || localName is not ("begin" or "end" or "dur")))?.Value;

    private static string? XmlId(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;

    /// <summary>Text of a <c>&lt;p&gt;</c>: whitespace collapsed (XML default), <c>br</c> as line breaks, styled spans as canonical tags.</summary>
    private static string TtmlText(XElement p, Dictionary<string, XElement> styles)
    {
        var sb = new StringBuilder();
        Append(p);
        return sb.ToString();

        void Append(XElement element)
        {
            var tags = StylesOf(element, styles);
            foreach (var t in tags) sb.Append('<').Append(t).Append('>');
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        sb.Append(Whitespace().Replace(text.Value, " "));
                        break;
                    case XElement { Name.LocalName: "br" }:
                        sb.Append('\n');
                        break;
                    case XElement { Name.LocalName: "span" } span:
                        Append(span);
                        break;
                }
            }
            for (var i = tags.Count - 1; i >= 0; i--) sb.Append("</").Append(tags[i]).Append('>');
        }
    }

    private static List<char> StylesOf(XElement element, Dictionary<string, XElement> styles)
    {
        var sources = new List<XElement> { element };
        if (Attr(element, "style") is { } refs)
            foreach (var id in refs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (styles.TryGetValue(id, out var s)) sources.Add(s);

        var tags = new List<char>();
        foreach (var source in sources)
        {
            if (Attr(source, "fontWeight") == "bold" && !tags.Contains('b')) tags.Add('b');
            if (Attr(source, "fontStyle") is "italic" or "oblique" && !tags.Contains('i')) tags.Add('i');
            var decoration = Attr(source, "textDecoration") ?? "";
            if (decoration.Contains("underline", StringComparison.Ordinal) && !decoration.Contains("noUnderline", StringComparison.Ordinal) && !tags.Contains('u')) tags.Add('u');
            if (decoration.Contains("lineThrough", StringComparison.Ordinal) && !decoration.Contains("noLineThrough", StringComparison.Ordinal) && !tags.Contains('s')) tags.Add('s');
        }
        return tags;
    }

    // ------------------------------------------------------------------ regexes

    [GeneratedRegex(@"<sync\b[^>]*?\bstart\s*=\s*[""']?(?<start>\d+)[""']?(?:[^>]*?\bend\s*=\s*[""']?(?<end>\d+)[""']?)?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SamiSync();

    [GeneratedRegex(@"<p\b(?:[^>]*?\bclass\s*=\s*[""']?(?<class>[A-Za-z0-9_-]+)[""']?)?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SamiParagraph();

    [GeneratedRegex(@"<title>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SamiTitle();

    [GeneratedRegex(@"</?(?:body|sami|html)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SamiBodyEnd();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:\[(?<tag>\d+:\d{1,2}(?:[.:]\d{1,3})?)\]\s*)+")]
    private static partial Regex LrcTimeTags();

    [GeneratedRegex(@"^(?<m>\d+):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?$")]
    private static partial Regex LrcTime();

    [GeneratedRegex(@"^\[(?<key>[A-Za-z#]+)\s*:(?<value>[^\]]*)\]$")]
    private static partial Regex LrcMeta();

    [GeneratedRegex(@"^\s*(?<h1>\d+):(?<m1>\d{1,2}):(?<s1>\d{1,2})[.,](?<f1>\d{1,3})\s*,\s*(?<h2>\d+):(?<m2>\d{1,2}):(?<s2>\d{1,2})[.,](?<f2>\d{1,3})\s*$")]
    private static partial Regex SbvTiming();

    [GeneratedRegex(@"^\{(?<start>\d+)\}\{(?<end>\d*)\}(?<text>.*)$")]
    private static partial Regex MicroDvdLine();

    [GeneratedRegex(@"\{(?<key>[A-Za-z]):(?<value>[^}]*)\}")]
    private static partial Regex MicroDvdCode();
}

/// <summary>
/// TTML time expressions with the document's frame and tick rates (<c>ttp:frameRate</c>, <c>ttp:frameRateMultiplier</c>,
/// <c>ttp:subFrameRate</c>, <c>ttp:tickRate</c>). Returns null for a missing or unreadable expression.
/// </summary>
internal sealed partial record TtmlTiming(double FrameRate = 30, double TickRate = 1, double SubFrameRate = 1)
{
    public static TtmlTiming From(XElement root)
    {
        string? Param(string name) => root.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
        static double? Number(string? s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

        var frameRate = Number(Param("frameRate"));
        var fps = frameRate ?? 30;
        if (Param("frameRateMultiplier") is { } multiplier)
        {
            var parts = multiplier.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && Number(parts[0]) is { } num && Number(parts[1]) is { } den) fps = fps * num / den;
        }
        var sub = Number(Param("subFrameRate")) ?? 1;
        var tick = Number(Param("tickRate")) ?? (frameRate is not null ? fps * sub : 1);
        return new TtmlTiming(fps, tick, sub);
    }

    public TimeSpan? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var s = expression.Trim();

        var clock = Clock().Match(s);
        if (clock.Success)
        {
            var h = double.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture);
            var m = double.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture);
            var sec = double.Parse(clock.Groups["s"].Value, CultureInfo.InvariantCulture);
            var seconds = (h * 60 + m) * 60 + sec;
            if (clock.Groups["frac"].Success) seconds += double.Parse("0." + clock.Groups["frac"].Value, CultureInfo.InvariantCulture);
            if (clock.Groups["frames"].Success)
            {
                var frames = double.Parse(clock.Groups["frames"].Value, CultureInfo.InvariantCulture);
                if (clock.Groups["sub"].Success) frames += double.Parse(clock.Groups["sub"].Value, CultureInfo.InvariantCulture) / SubFrameRate;
                seconds += frames / FrameRate;
            }
            return Ms(seconds);
        }

        var offset = Offset().Match(s);
        if (!offset.Success) return null;
        var value = double.Parse(offset.Groups["v"].Value, CultureInfo.InvariantCulture);
        return offset.Groups["u"].Value switch
        {
            "h" => Ms(value * 3600),
            "m" => Ms(value * 60),
            "s" => Ms(value),
            "ms" => Ms(value / 1000),
            "f" => Ms(value / FrameRate),
            _ => Ms(value / TickRate), // "t"
        };

        static TimeSpan Ms(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
    }

    // hh:mm:ss, hh:mm:ss.fraction (a comma is accepted too), hh:mm:ss:frames, hh:mm:ss:frames.subframes
    [GeneratedRegex(@"^(?<h>\d+):(?<m>\d{2}):(?<s>\d{2})(?:[.,](?<frac>\d+)|:(?<frames>\d+)(?:\.(?<sub>\d+))?)?$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?<v>\d+(?:\.\d+)?)(?<u>h|ms|m|s|f|t)$")]
    private static partial Regex Offset();
}
